using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

/// <summary>
/// GPU mode: the 4 user-visible modes in the UI.
/// </summary>
public enum GpuMode
{
    Eco = 0,
    Standard = 1,
    Optimized = 2,
    Ultimate = 3
}

/// <summary>
/// Result of a GPU mode switch attempt. The UI uses this to decide
/// what notification/dialog/tip to show.
/// </summary>
public enum GpuSwitchResult
{
    /// <summary>Mode applied immediately - hardware state changed.</summary>
    Applied,
    /// <summary>Hardware already in the desired state - no write needed.</summary>
    AlreadySet,
    /// <summary>MUX change latched - reboot required to take effect.</summary>
    RebootRequired,
    /// <summary>dGPU driver is active - cannot safely write dgpu_disable=1.
    /// UI should show confirmation dialog (Switch Now / After Reboot / Cancel).</summary>
    DriverBlocking,
    /// <summary>Mode saved to config for next reboot (user chose "After Reboot").</summary>
    Deferred,
    /// <summary>Write failed (sysfs error, permission denied, etc.).</summary>
    Failed,
    /// <summary>Eco mode blocked - MUX was set to 0 (Ultimate) this boot session. Reboot first.</summary>
    EcoBlocked,
    /// <summary>dgpu_disable=0 was written but the dGPU did not re-enumerate on the
    /// PCI bus after rescan (slow/stuck firmware). UI should advise a reboot.</summary>
    DgpuReenableFailed
}

/// <summary>
/// Centralized GPU mode switching controller.
///
/// Every danger in this system comes from a single operation: writing dgpu_disable=1
/// while the dGPU driver is loaded and active. Everything else is either instant or
/// just requires a reboot. The entire safety architecture exists to protect that one write.
///
/// Architecture: button handlers and tray menu call this controller. They never write
/// sysfs directly. The controller reads current hardware state, computes the delta,
/// and executes the needed operations with safety checks.
///
/// See GPU_MODE_PLAN.md for the complete scenario matrix and flows.
/// </summary>
public class GpuModeController
{
    private readonly IAsusWmi _wmi;
    private readonly IPowerManager _power;

    /// <summary>Lock to prevent concurrent GPU mode operations.
    /// Writing dgpu_disable can block for 30-60 seconds - we must not queue multiple writes.
    /// SemaphoreSlim(1,1) provides atomic check-and-acquire, eliminating the TOCTOU race
    /// that existed with the previous volatile bool approach.</summary>
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    /// <summary>
    /// Tracks pending MUX latch value within this session.
    /// After SetGpuMuxMode(x), hardware still reports the OLD value until reboot.
    /// This field remembers what we latched so ComputeAndExecute() and ScheduleModeForReboot()
    /// use the correct effective MUX, not the stale hardware readback.
    /// -1 = no pending latch (use hardware value).
    /// </summary>
    private volatile int _pendingMuxLatch = -1;

    /// <summary>Cached dGPU PCI address for AMD systems (e.g., "0000:01:00.0").</summary>
    private string? _cachedDgpuPciAddress;
    private bool _dgpuPciScanned;

    /// <summary>
    /// Invoked after the controller mutates PCI bus topology in-process
    /// (the live PCI Eco-to-Standard transition rescans /sys/bus/pci so
    /// the dGPU reappears without a reboot).
    /// </summary>
    public static Action? OnLivePciTransition;

    /// <summary>
    /// Invoked after the dGPU is re-enabled to re-apply (or reset) the current
    /// mode's GPU tuning. Wired by App to ModeControl; left null in headless
    /// contexts (tests) so the controller stays free of UI-layer dependencies.
    /// </summary>
    public static Action? OnReapplyGpuTuning;

    public GpuModeController(IAsusWmi wmi, IPowerManager power)
    {
        _wmi = wmi;
        _power = power;
    }

    /// <summary>True if a GPU mode switch is currently in progress (sysfs write blocking).</summary>
    public bool IsSwitchInProgress => _switchLock.CurrentCount == 0;

    /// <summary>
    /// Return the effective MUX value: if we latched a change this session, return
    /// the latched value. Otherwise return actual hardware state.
    /// Used by ComputeAndExecute(), ExecuteDisableDgpu(), ScheduleModeForReboot()
    /// methods that need to know "what MUX will be after reboot".
    /// NOT used by GetCurrentMode(), AutoGpuSwitch(), IsPendingReboot(),
    /// ApplyPendingOnStartup(), ApplyPendingOnShutdown() - those need actual hardware.
    /// </summary>
    private int GetEffectiveMux()
    {
        int pending = _pendingMuxLatch;
        return pending >= 0 ? pending : _wmi.GetGpuMuxMode();
    }

    /// <summary>
    /// Check if MUX=0 was written at any point during the current boot session.
    /// Uses persistent storage (AppConfig) keyed by boot_id, so it survives app restarts
    /// within the same boot session but auto-clears after reboot.
    /// </summary>
    private static bool IsMuxZeroLatchedThisBoot()
    {
        try
        {
            string? stored = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(stored))
                return false;
            string current = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return stored == current;
        }
        catch (IOException)
        {
            // /proc/sys/kernel/random/boot_id not readable (container/chroot) - fail-open
            return false;
        }
    }

    /// <summary>
    /// Persist the MUX=0 latch flag for the current boot session.
    /// Called after every successful SetGpuMuxMode(0) write.
    /// The flag stays set until reboot (boot_id changes).
    /// </summary>
    private static void SetMuxZeroLatchFlag()
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            AppConfig.Set("mux_zero_latched_boot_id", bootId);
            Logger.WriteLine($"GpuModeController: MUX=0 latch persisted - boot_id={bootId}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: failed to persist MUX=0 latch flag: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the persistent MUX=0 latch flag (config "mux_zero_latched_boot_id")
    /// when the stored boot_id no longer matches /proc/sys/kernel/random/boot_id.
    /// The flag is intentionally session-scoped - it exists to make
    /// WouldCreateImpossibleState resilient to app restarts within the same
    /// boot, but must NOT leak across reboots or across backend switches.
    ///
    /// Called from ApplyPendingOnStartup before any backend-specific code so
    /// PCI users also benefit; otherwise WouldCreateImpossibleState would
    /// permanently refuse Eco on any system that previously latched Ultimate.
    /// </summary>
    private static void ClearStaleMuxLatchFlag()
    {
        try
        {
            string? storedBootId = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(storedBootId))
                return;
            string currentBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (storedBootId != currentBootId)
            {
                AppConfig.Set("mux_zero_latched_boot_id", "");
                Logger.WriteLine("GpuModeController: reboot detected - cleared MUX=0 latch flag");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: MUX=0 latch reboot check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// SAFETY: Would the given target mode create an impossible post-reboot state?
    /// The ONE impossible state: MUX=0 (dGPU sole display) + dgpu_disable=1 (dGPU off)
    /// = guaranteed black screen.
    ///
    /// Defense in depth: checks BOTH the persistent boot_id flag (survives app restart)
    /// AND the in-memory effective MUX latch (fast path, same session).
    /// Either check returning true blocks Eco.
    /// </summary>
    private bool WouldCreateImpossibleState(GpuMode target)
    {
        if (target != GpuMode.Eco)
            return false;

        // Check persistent flag first (survives app restart within same boot)
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GpuModeController: IMPOSSIBLE STATE PREVENTED - MUX=0 was written this boot session (persistent flag). Eco + MUX=0 = black screen.");
            return true;
        }

        // Also check in-memory latch (fast path, same session - defense in depth)
        int effectiveMux = GetEffectiveMux();
        if (effectiveMux == 0)
        {
            Logger.WriteLine("GpuModeController: IMPOSSIBLE STATE PREVENTED - cannot schedule Eco when MUX is latched to 0 (Ultimate). Eco + MUX=0 = black screen.");
            return true;
        }
        return false;
    }

    // Public API

    /// <summary>
    /// The single entry point for all GPU mode switches (buttons + tray menu).
    /// Determines current hardware state, computes needed operations, executes
    /// with safety checks. Returns result for UI to act on.
    ///
    /// NOTE: This may block for 30-60 seconds (dgpu_disable write).
    /// Always call from a background thread (Task.Run), never from the UI thread.
    /// </summary>
    public GpuSwitchResult RequestModeSwitch(GpuMode target)
    {
        if (!_switchLock.Wait(0))
        {
            // A hardware switch is blocking - can't start another.
            // But save the user's latest choice so it applies after reboot.
            // This ensures rapid clicks always result in the LAST choice winning.
            Logger.WriteLine($"GpuModeController: switch in progress, scheduling {target} for reboot");
            var scheduleResult = ScheduleModeForReboot(target);
            if (scheduleResult == GpuSwitchResult.EcoBlocked)
                return GpuSwitchResult.EcoBlocked;
            return GpuSwitchResult.Deferred;
        }

        try
        {
            Logger.WriteLine($"GpuModeController: RequestModeSwitch → {target}");
            var result = ComputeAndExecute(target);

            // If the user has the AURA "GPU Mode" color effect selected,
            // refresh the keyboard so the new GPU mode color is visible
            // immediately. Failure here is non-fatal - log and continue.
            if (result == GpuSwitchResult.Applied || result == GpuSwitchResult.AlreadySet)
            {
                try
                {
                    if ((USB.AuraMode)AppConfig.Get("aura_mode") == USB.AuraMode.GpuMode)
                        USB.CustomRgb.ApplyGpuColor();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GpuModeController: AURA GpuMode refresh failed: {ex.Message}");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: RequestModeSwitch({target}) failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Attempt to release the GPU driver (pkexec rmmod / PCI unbind) and then
    /// write dgpu_disable=1. Called from the "Switch Now" confirmation dialog button.
    ///
    /// NOTE: This may show a polkit password dialog and block for a while.
    /// Always call from a background thread.
    /// </summary>
    public GpuSwitchResult TryReleaseAndSwitch()
    {
        if (!_switchLock.Wait(0))
            return GpuSwitchResult.AlreadySet;

        try
        {
            Logger.WriteLine("GpuModeController: TryReleaseAndSwitch - attempting driver release");
            LogHoldersSnapshot("pre-release");

            bool released = TryReleaseGpuDriver();
            if (!released)
            {
                Logger.WriteLine("GpuModeController: driver release failed - deferring to reboot");
                SaveModeToConfig(GpuMode.Eco);
                // If we're in Ultimate (MUX=0), latch MUX→1 first so next boot can apply Eco
                int effectiveMux = GetEffectiveMux();
                if (effectiveMux == 0)
                {
                    // gpu_mux_mode write fails when dgpu_disable=1 - enable dGPU first if needed
                    bool ecoActive = _wmi.GetGpuEco();
                    if (ecoActive)
                    {
                        Logger.WriteLine("GpuModeController: TryRelease - enabling dGPU before MUX latch");
                        try
                        { _wmi.SetGpuEco(false); RemoveDriverBlock(); }
                        catch (Exception muxEx)
                        {
                            Logger.WriteLine($"GpuModeController: TryRelease - exit Eco failed: {muxEx.Message}");
                        }
                    }
                    Logger.WriteLine("GpuModeController: MUX=0, latching MUX→1 for Eco boot");
                    try
                    {
                        _wmi.SetGpuMuxMode(1);
                        _pendingMuxLatch = 1;
                    }
                    catch (Exception muxEx)
                    {
                        Logger.WriteLine($"GpuModeController: TryRelease - MUX latch failed: {muxEx.Message}");
                    }
                }
                // pkexec auth is cached from rmmod attempt - write block without re-prompting
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }

            // Driver released - now write dgpu_disable=1 (should be fast)
            Logger.WriteLine("GpuModeController: driver released, writing dgpu_disable=1");
            _wmi.SetGpuEco(true);

            // Verify
            if (_wmi.GetGpuEco())
            {
                SaveModeToConfig(GpuMode.Eco);
                // Eco applied live - remove block artifacts (dgpu_disable=1 is persistent)
                RemoveDriverBlock();
                // Hide the NVIDIA Vulkan ICD while the dGPU is disabled.
                ApplyVulkanIcd(dgpuAvailable: false);
                Logger.WriteLine("GpuModeController: Eco mode applied after driver release");
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GpuModeController: dgpu_disable write succeeded but readback != 1");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: TryReleaseAndSwitch failed: {ex.Message}");
            SaveModeToConfig(GpuMode.Eco);
            return GpuSwitchResult.Deferred;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Save desired mode to config for next reboot. Latches any MUX changes.
    /// Called from the "After Reboot" confirmation dialog button.
    /// Does NOT write dgpu_disable.
    /// Returns the result so callers can distinguish EcoBlocked from RebootRequired.
    /// </summary>
    public GpuSwitchResult ScheduleModeForReboot(GpuMode target)
    {
        Logger.WriteLine($"GpuModeController: ScheduleModeForReboot({target})");

        // SAFETY: If scheduling Eco but MUX is latched to 0, the user changed from
        // Ultimate to Eco without rebooting. After reboot MUX=0 + dgpu_disable=1 = black screen.
        // Refuse: keep config as-is, remove any stale Eco artifacts.
        if (WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GpuModeController: ScheduleModeForReboot REFUSED - would create impossible post-reboot state (Eco + MUX=0)");
            Logger.WriteLine("GpuModeController: user must reboot into Ultimate first, THEN switch to Eco");
            RemoveDriverBlock();
            return GpuSwitchResult.EcoBlocked;
        }

        SaveModeToConfig(target);

        // If target needs MUX change, latch it now (instant, safe)
        // Use GetEffectiveMux() - if we already latched a MUX change this session,
        // we need to know the LATCHED value, not the stale hardware readback.
        int effectiveMux = GetEffectiveMux();
        int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;

        if (effectiveMux >= 0 && effectiveMux != targetMux)
        {
            // gpu_mux_mode write fails when dgpu_disable=1 - enable dGPU first
            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: ScheduleModeForReboot - enabling dGPU before MUX latch");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GpuModeController: ScheduleModeForReboot - failed to exit Eco: {ex.Message}");
                    // Can't latch MUX, but config is saved - ApplyPendingOnStartup will retry
                    WriteDriverBlock(target);
                    return GpuSwitchResult.RebootRequired;
                }
            }

            Logger.WriteLine($"GpuModeController: latching MUX {effectiveMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
                _pendingMuxLatch = targetMux;
                if (targetMux == 0)
                    SetMuxZeroLatchFlag();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: ScheduleModeForReboot - MUX write failed: {ex.Message}");
                // Config is saved - ApplyPendingOnStartup will retry
            }
        }

        // Write driver block so Eco can be applied safely after reboot.
        // For non-Eco targets, this removes any stale block artifacts.
        WriteDriverBlock(target);
        return GpuSwitchResult.RebootRequired;
    }

    /// <summary>
    /// On startup: check if config has a pending GPU mode that differs from
    /// current hardware state. If so, try to apply it.
    ///
    /// NOTE: This may block. Call from a background thread.
    /// </summary>
    public GpuSwitchResult ApplyPendingOnStartup()
    {
        // Clear stale MUX=0 latch flag on reboot detection. Runs FIRST,
        // before any backend-specific path, because the persistent flag is
        // session-scoped and must not leak across boots regardless of which
        // backend the user is on. Skipping this lets WouldCreateImpossibleState
        // misfire (Eco refused with "MUX=0 was written this boot session"
        // even though we are in a fresh boot or in PCI mode where MUX is
        // irrelevant).
        ClearStaleMuxLatchFlag();

        if (AppConfig.NoGpu() || AppConfig.IsAMDiGPU())
        {
            Logger.WriteLine("GpuModeController: APU-only system (NoGpu/IsAMDiGPU) - skipping startup GPU probe");
            return GpuSwitchResult.AlreadySet;
        }

        // PCI backend: the boot service is solely responsible for applying
        // any pending mode at boot. By the time ghelper starts up, the
        // transition has already happened (or failed and been recorded in
        // /etc/ghelper/last-eco-failed). Just sync config with the actual
        // file state so the UI shows the right active mode, no firmware
        // pokes needed.
        if (AppConfig.IsPciGpuBackend())
        {
            // Do NOT touch mux_zero_latched_boot_id here. The shared
            // ClearStaleMuxLatchFlag() above already cleared it on a
            // cross-boot stale match; anything still set is from THIS
            // boot session and represents a genuine pending firmware
            // latch that must keep blocking PCI Eco until the user
            // reboots (else the next boot lands in MUX=0 + udev-removed
            // dGPU = black screen). Same logic for _pendingMuxLatch.

            GpuMode actual = GetCurrentMode();
            string? saved = AppConfig.GetString("gpu_mode");
            if (saved != actual.ToString().ToLowerInvariant())
            {
                SaveModeToConfig(actual);
                Logger.WriteLine($"GpuModeController: PCI backend startup - synced config gpu_mode='{actual}' to match block-file state");
            }
            return GpuSwitchResult.AlreadySet;
        }

        // Boot safety check (supergfxctl pattern)
        // If MUX=0 (Ultimate/dGPU-direct) AND dgpu_disable=1, that's an impossible
        // state that causes boot hangs. Force dgpu_disable=0 to recover.
        // This shouldn't happen with the modprobe.d approach but could occur from
        // manual sysfs tinkering or stale tmpfiles from a previous version.
        BootSafetyCheck();

        string? savedMode = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(savedMode))
        {
            // No pending mode - clean up any stale block artifacts (crash, uninstall, etc.)
            RemoveDriverBlock();
            return GpuSwitchResult.AlreadySet;
        }

        GpuMode target = ParseGpuMode(savedMode);
        GpuMode current = GetCurrentMode();

        // Check if hardware matches desired mode
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        bool needsDgpuChange = false;
        bool needsMuxChange = false;

        if (target == GpuMode.Eco && !ecoEnabled)
        {
            if (mux == 0)
            {
                // MUX=0 (Ultimate) - kernel refuses dgpu_disable=1 in this mode.
                // Latch MUX=1 first, then Eco will apply on the NEXT reboot.
                needsMuxChange = true;
            }
            else
            {
                needsDgpuChange = true;
            }
        }
        else if (target == GpuMode.Ultimate && mux != 0)
            needsMuxChange = true;
        else if (target == GpuMode.Standard && mux == 0)
            needsMuxChange = true;
        else if (target == GpuMode.Optimized)
        {
            // Optimized needs MUX=1 first
            if (mux == 0)
                needsMuxChange = true;
            // Optimized with hardware in Eco - need to enable dGPU
            else if (ecoEnabled)
                needsDgpuChange = true;
        }
        else if ((target == GpuMode.Standard) && ecoEnabled)
        {
            // Config says Standard but hardware is Eco (rapid-click override scenario).
            // Need to enable dGPU (dgpu_disable=0).
            needsDgpuChange = true;
        }

        if (!needsDgpuChange && !needsMuxChange)
        {
            Logger.WriteLine($"GpuModeController: startup - hardware matches saved mode '{savedMode}'");
            // Hardware matches - clean up block artifacts if they exist (mode was applied)
            RemoveDriverBlock();
            return GpuSwitchResult.AlreadySet;
        }

        if (needsMuxChange)
        {
            // If in Eco, must enable dGPU before MUX change
            // (firmware rejects gpu_mux_mode write when dgpu_disable=1)
            if (ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: startup - enabling dGPU before MUX change");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GpuModeController: startup - failed to exit Eco: {ex.Message}");
                    return GpuSwitchResult.Failed;
                }
            }

            int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;
            Logger.WriteLine($"GpuModeController: startup - latching MUX → {targetMux} for '{savedMode}'");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup - MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }

        // needsDgpuChange - two directions:
        // (a) Target is Eco but hardware is not Eco → disable dGPU
        // (b) Target is Standard/Optimized but hardware is Eco → enable dGPU
        bool targetEco = (target == GpuMode.Eco);

        if (targetEco)
        {
            // Direction (a): Apply pending Eco
            Logger.WriteLine("GpuModeController: startup - applying pending Eco mode");

            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GpuModeController: startup - dGPU driver active, cannot apply Eco");
                // MUX is correct (1) but dGPU driver is loaded - write block so NEXT boot
                // driver won't load, then ghelper can write dgpu_disable=1 safely.
                // This breaks the infinite loop: startup → driver active → can't apply → repeat.
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.DriverBlocking;
            }

            // Driver not active - safe to write
            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GpuModeController: startup - switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(true);

                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GpuModeController: startup - Eco mode applied successfully");
                    // Eco confirmed - remove block artifacts (dgpu_disable=1 is persistent)
                    RemoveDriverBlock();
                    return GpuSwitchResult.Applied;
                }
                else
                {
                    Logger.WriteLine("GpuModeController: startup - dgpu_disable write failed readback");
                    return GpuSwitchResult.Failed;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup apply failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else
        {
            // Direction (b): Enable dGPU for pending Standard/Optimized
            // Hardware is in Eco (dgpu_disable=1) but config says Standard/Optimized.
            // This happens when rapid clicks override a blocking Eco switch.
            Logger.WriteLine($"GpuModeController: startup - enabling dGPU for pending {target} (hardware is Eco)");

            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GpuModeController: startup - switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(false); // Always safe - enables dGPU
                RemoveDriverBlock();    // Clean up stale block artifacts
                Logger.WriteLine($"GpuModeController: startup - dGPU enabled for {target}");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup enable dGPU failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
    }

    /// <summary>
    /// On shutdown (SIGTERM/SIGINT): best-effort dgpu_disable=1 if Eco is pending.
    /// Still checks driver safety - SIGINT is not a system shutdown (Xorg is still running),
    /// and even during SIGTERM the display stack may not have released the GPU yet.
    /// If driver is active, skip - ApplyPendingOnStartup() will try on next boot.
    /// </summary>
    public void ApplyPendingOnShutdown()
    {
        try
        {
            // PCI backend: the boot service applies pending modes on the
            // NEXT startup, not on shutdown. There is no live dgpu_disable
            // path to take here. Skip silently so we don't accidentally
            // call into the WMI sysfs layer on non-ASUS systems where it
            // does not exist.
            if (AppConfig.IsPciGpuBackend())
                return;

            string? savedMode = AppConfig.GetString("gpu_mode");
            if (savedMode != "eco")
                return;

            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled)
                return; // Already in Eco

            // MUX=0 guard - kernel refuses dgpu_disable=1 when in Ultimate mode
            int mux = _wmi.GetGpuMuxMode();
            if (mux == 0)
            {
                Logger.WriteLine("GpuModeController: shutdown - MUX=0 (Ultimate), cannot write dgpu_disable");
                Logger.WriteLine("GpuModeController: Eco mode requires MUX=1 first - will handle on next startup");
                return;
            }

            // Safety check - same as everywhere else.
            // Writing dgpu_disable=1 while the driver is active triggers ACPI PCI hot-removal
            // which causes a kernel panic (NULL deref in nvidia_modeset when Xorg still has the GPU).
            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GpuModeController: shutdown - dGPU driver still active, skipping dgpu_disable write");
                Logger.WriteLine("GpuModeController: Eco mode will be applied on next startup instead");
                return;
            }

            Logger.WriteLine("GpuModeController: shutdown - driver idle, writing dgpu_disable=1 for pending Eco");
            _wmi.SetGpuEco(true);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: shutdown apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Optimized mode auto Eco/Standard switch on power state change.
    /// Same safety as RequestModeSwitch but never shows a dialog - returns
    /// DriverBlocking for caller to show a notification instead.
    ///
    /// NOTE: May block for 30-60 seconds. Call from background thread.
    /// </summary>
    public GpuSwitchResult AutoGpuSwitch()
    {
        if (!AppConfig.IsOptimizedGpuModeEnabled())
            return GpuSwitchResult.AlreadySet;

        if (!AppConfig.Is("gpu_auto"))
            return GpuSwitchResult.AlreadySet;

        // PCI backend has no live switching path - Optimized auto-toggle is
        // meaningless when every transition requires a reboot. The UI hides
        // the Optimized button in PCI mode, so this is a defensive guard.
        if (AppConfig.IsPciGpuBackend())
        {
            Logger.WriteLine("GpuModeController: AutoGpuSwitch - PCI backend, no live switching available");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch if in Ultimate (MUX=0)
        int mux = _wmi.GetGpuMuxMode();
        if (mux == 0)
        {
            Logger.WriteLine("GpuModeController: AutoGpuSwitch - MUX=0 (Ultimate), skipping");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch to Eco if MUX=0 was latched this boot (persistent flag)
        // Hardware may still read MUX=1, but firmware has MUX=0 pending - Eco would be impossible
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GpuModeController: AutoGpuSwitch - MUX=0 latched this boot, Eco path blocked - staying in Standard");
            return GpuSwitchResult.AlreadySet;
        }

        bool onAc = _power.IsOnAcPower();
        bool ecoEnabled = _wmi.GetGpuEco();

        if (onAc && ecoEnabled)
        {
            // Plugged in → enable dGPU (always safe)
            Logger.WriteLine("GpuModeController: AutoGpuSwitch - AC power, enabling dGPU");
            if (!_switchLock.Wait(0))
                return GpuSwitchResult.AlreadySet;

            try
            {
                _wmi.SetGpuEco(false);
                // Switching away from Eco - clean up block artifacts
                RemoveDriverBlock();
                Logger.WriteLine("GpuModeController: AutoGpuSwitch - dGPU enabled");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: AutoGpuSwitch Eco→Standard failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else if (!onAc && !ecoEnabled)
        {
            // On battery → disable dGPU (THE dangerous path)
            Logger.WriteLine("GpuModeController: AutoGpuSwitch - battery, attempting Eco");
            if (!_switchLock.Wait(0))
                return GpuSwitchResult.AlreadySet;

            try
            {
                return ExecuteDisableDgpu();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: AutoGpuSwitch battery→Eco failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }

        return GpuSwitchResult.AlreadySet;
    }

    /// <summary>
    /// Read current hardware state + config → return the current mode enum.
    /// </summary>
    public GpuMode GetCurrentMode()
    {
        // Block files (modprobe blacklist + udev hot-remove rule) are the
        // persistent Eco state for the PCI backend. They are also written
        // briefly during an ASUS-WMI eco transition. Either way, when they
        // exist the dGPU is effectively disabled - it is hot-removed by
        // udev on every boot, so `dgpu_disable=0` is meaningless until the
        // blocks are removed. Treat their presence as the source of truth
        // for "in Eco" regardless of the configured backend, so the UI
        // doesn't claim Standard while reality is "dGPU vanished from the
        // PCI bus".
        bool blocksPresent = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);

        // PCI backend: blocks are the only signal. No MUX, no auto, no
        // firmware sysfs to query.
        if (AppConfig.IsPciGpuBackend())
            return blocksPresent ? GpuMode.Eco : GpuMode.Standard;

        bool gpuAuto = AppConfig.Is("gpu_auto");
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        if (mux == 0)
            return GpuMode.Ultimate;
        if (gpuAuto)
            return GpuMode.Optimized;
        // Either dgpu_disable=1 live OR PCI-style blocks still on disk →
        // Eco. The latter happens after the user toggled the backend from
        // PCI to asus-wmi while still in eco: dgpu_disable reads 0 but the
        // dGPU is gone from the PCI bus until the user explicitly switches
        // to Standard (which removes the blocks via RemoveDriverBlock).
        if (ecoEnabled || blocksPresent)
            return GpuMode.Eco;
        return GpuMode.Standard;
    }

    /// <summary>
    /// True if config gpu_mode differs from current hardware state
    /// (mode is waiting for a reboot to take effect).
    /// </summary>
    public bool IsPendingReboot()
    {
        // PCI backend: the trigger file is the single source of truth.
        // Boot script applies and removes it; while present, a reboot is pending.
        if (AppConfig.IsPciGpuBackend())
            return File.Exists(TriggerPath);

        string? saved = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(saved))
            return false;

        GpuMode target = ParseGpuMode(saved);
        GpuMode current = GetCurrentMode();

        // Simple check: if they differ, something is pending
        if (target == current)
            return false;

        // More precise: check if the difference requires a reboot
        int mux = _wmi.GetGpuMuxMode();
        bool eco = _wmi.GetGpuEco();

        return target switch
        {
            GpuMode.Eco => !eco,        // Eco pending but not applied
            GpuMode.Ultimate => mux != 0, // MUX change pending
            GpuMode.Standard => mux == 0, // Coming from Ultimate, MUX pending
            GpuMode.Optimized => mux == 0, // Coming from Ultimate, MUX pending
            _ => false
        };
    }

    // Core logic

    /// <summary>
    /// Core logic: read current hardware, compute delta, route to Execute* methods.
    /// </summary>
    private GpuSwitchResult ComputeAndExecute(GpuMode target)
    {
        // PCI backend short-circuits the WMI matrix entirely. The
        // modprobe + udev files ARE the persistent Eco state and the boot
        // script handles the actual transition on the next reboot. We only
        // need to write the right trigger / block artifacts here.
        if (AppConfig.IsPciGpuBackend())
            return ComputeAndExecutePci(target);

        // 4×4 Transition Matrix
        // From\To     | Eco         | Standard    | Optimized   | Ultimate
        // Eco         | AlreadySet  | Applied     | Applied*    | RebootReq
        // Standard    | DANGER†     | AlreadySet  | Applied*/†  | RebootReq
        // Optimized   | (delegates) | (delegates) | AlreadySet  | RebootReq‡
        // Ultimate    | Multi-boot§ | RebootReq   | RebootReq   | AlreadySet
        //
        // * Optimized target: targetEco depends on AC power (battery=Eco hw, AC=Standard hw)
        // † DANGER: dgpu_disable=1 when driver active → DriverBlocking dialog
        // ‡ If Optimized hw=Eco, exits Eco first (200ms verify), then MUX write
        // § Ultimate→Eco: MUX latch + DriverBlocking or deferred Eco (2-boot path)
        //
        // Config is saved only on success paths (Applied, RebootRequired, AlreadySet,
        // DriverBlocking). NEVER saved on Failed - prevents stale config from rejected writes.

        // Read current hardware state
        bool currentEco = _wmi.GetGpuEco();     // true if dgpu_disable=1
        // Use effective MUX (accounts for pending latch from earlier this session)
        int currentMux = GetEffectiveMux();       // 0=Ultimate, 1=hybrid

        // Compute target hardware state
        bool targetEco;
        int targetMux;
        bool targetAuto = false;

        switch (target)
        {
            case GpuMode.Eco:
                targetEco = true;
                targetMux = 1;
                break;
            case GpuMode.Standard:
                targetEco = false;
                targetMux = 1;
                break;
            case GpuMode.Optimized:
                targetAuto = true;
                targetMux = 1;
                // On AC: want dGPU on (Standard hw). On battery: want dGPU off (Eco hw).
                targetEco = !_power.IsOnAcPower();
                break;
            case GpuMode.Ultimate:
                targetEco = false;
                targetMux = 0;
                break;
            default:
                return GpuSwitchResult.Failed;
        }

        // gpu_auto is a software flag (no hardware write) - safe to set early
        AppConfig.Set("gpu_auto", targetAuto ? 1 : 0);
        // NOTE: SaveModeToConfig is called at each SUCCESS exit point below, not here.
        // If a hardware write fails, config must NOT say the new mode.

        // Exit Eco first if MUX change is needed
        // gpu_mux_mode write FAILS when dgpu_disable=1 (firmware rejects "No such device").
        // Must enable dGPU before any MUX change.
        if (currentEco && currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine("GpuModeController: currently in Eco, enabling dGPU before MUX change");
            try
            {
                _wmi.SetGpuEco(false);
                RemoveDriverBlock();

                // Verify dGPU re-enablement - Windows G-Helper pattern: wait + readback.
                // SetGpuEco(false) includes 50ms settle + PCI rescan (Phase 1).
                // Additional 200ms here for firmware to update dgpu_disable readback.
                Thread.Sleep(200);
                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GpuModeController: FAILED to exit Eco - dgpu_disable still reads 1 after 200ms, aborting MUX change");
                    return GpuSwitchResult.Failed;
                }

                currentEco = false;
                Logger.WriteLine("GpuModeController: dGPU re-enabled, dgpu_disable readback confirmed 0");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: failed to exit Eco before MUX change: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
        }

        // MUX change needed?
        if (currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine($"GpuModeController: MUX change {currentMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (InvalidOperationException ex)
            {
                // Safety guard violation (dgpu_disable=1) - shouldn't happen after Eco exit above
                Logger.WriteLine($"GpuModeController: MUX write safety violation: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            catch (Exception ex)
            {
                // IOException from firmware rejection, or other unexpected error
                Logger.WriteLine($"GpuModeController: MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            _pendingMuxLatch = targetMux;
            if (targetMux == 0)
                SetMuxZeroLatchFlag();

            if (currentEco != targetEco && targetEco)
            {
                // MUX change + Eco needed - BUT validate this isn't an impossible combo.
                // If MUX is latched to 0 (Ultimate), Eco block artifacts would cause black screen.
                // WriteDriverBlock already refuses, but be explicit here too.
                if (WouldCreateImpossibleState(target))
                {
                    // MUX latched to 0 + Eco = impossible. This shouldn't happen from
                    // normal UI flow (Eco has targetMux=1), but defend against it.
                    Logger.WriteLine("GpuModeController: MUX change + Eco creates impossible state - Eco blocked");
                    RemoveDriverBlock();
                    return GpuSwitchResult.EcoBlocked;
                }

                // Check if driver is blocking.
                // If driver is active, return DriverBlocking so the UI shows the dialog
                // on the FIRST click (not silently latching MUX and forcing a second click).
                if (IsDgpuDriverActive())
                {
                    Logger.WriteLine("GpuModeController: MUX change + Eco needed, driver active → DriverBlocking");
                    // MUX is already latched above. The dialog's "After Reboot" will call
                    // ScheduleModeForReboot() which writes the block artifacts.
                    // ScheduleModeForReboot also has the impossible-state guard.
                    SaveModeToConfig(target);
                    return GpuSwitchResult.DriverBlocking;
                }
                Logger.WriteLine("GpuModeController: also need Eco - deferred to after MUX settles (next boot)");
            }
            else if (!targetEco)
            {
                // Switching to Standard/Ultimate/Optimized - clean up stale block artifacts
                RemoveDriverBlock();
            }

            SaveModeToConfig(target);
            return GpuSwitchResult.RebootRequired;
        }

        // dgpu change needed?
        if (currentEco == targetEco)
        {
            Logger.WriteLine($"GpuModeController: hardware already in target state (eco={currentEco})");
            // Clean up stale block artifacts if target is not Eco
            if (!targetEco)
                RemoveDriverBlock();
            SaveModeToConfig(target);
            return GpuSwitchResult.AlreadySet;
        }

        if (!targetEco)
        {
            // Enabling dGPU - always safe, always fast
            var result = ExecuteEnableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            return result;
        }
        else
        {
            // Disabling dGPU - THE dangerous path
            var result = ExecuteDisableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            else if (result == GpuSwitchResult.DriverBlocking || result == GpuSwitchResult.RebootRequired)
                SaveModeToConfig(target);
            // On Failed: do NOT save config
            return result;
        }
    }

    /// <summary>
    /// PCI backend switching. There are only two effective modes - Eco
    /// (block artifacts present) and Standard (no block artifacts). The
    /// transition is always deferred to reboot; the boot script does the
    /// actual rmmod / PCI rescan work. Optimized and Ultimate fall through
    /// to Standard since they have no meaning without ASUS firmware.
    /// </summary>
    private GpuSwitchResult ComputeAndExecutePci(GpuMode target)
    {
        // Optimized / Ultimate are not meaningful in PCI mode. Treat them as
        // Standard so the dGPU is enabled at next boot. The UI should be
        // hiding these buttons but the controller stays defensive in case
        // the tray menu or a config import triggers them.
        if (target == GpuMode.Optimized || target == GpuMode.Ultimate)
        {
            Logger.WriteLine($"GpuModeController: PCI backend - {target} not applicable, treating as Standard");
            target = GpuMode.Standard;
        }

        bool ecoBlocksPresent = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);
        bool wantEco = (target == GpuMode.Eco);
        bool wantStandard = (target == GpuMode.Standard);

        // Already in the desired persistent state and no pending switch?
        // Mirror the WMI flow and report AlreadySet so the UI clears its
        // "reboot pending" tip.
        if (!File.Exists(TriggerPath))
        {
            if (wantEco && ecoBlocksPresent)
            {
                Logger.WriteLine("GpuModeController: PCI backend - already in Eco (blocks present), no-op");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.AlreadySet;
            }
            if (wantStandard && !ecoBlocksPresent)
            {
                Logger.WriteLine("GpuModeController: PCI backend - already in Standard (no blocks), no-op");
                SaveModeToConfig(GpuMode.Standard);
                return GpuSwitchResult.AlreadySet;
            }
        }

        // Live Eco → Standard transition. Removing the persistent blocks +
        // reloading udev + rescanning PCI brings the dGPU back online
        // without a reboot, mirroring the asus-wmi SetGpuEco(false) live
        // path. Only Eco→Standard can be live; going INTO Eco still
        // requires a reboot because rmmod nvidia would fail while Xorg
        // holds the GPU. On any failure (sudo cancelled, sysfs read-only)
        // we fall through to the deferred reboot path below so the user
        // can still get out of Eco eventually.
        if (wantStandard && ecoBlocksPresent)
        {
            var live = TryLiveRemovePciBlocks();
            if (live == GpuSwitchResult.Applied)
            {
                SaveModeToConfig(GpuMode.Standard);
                Logger.WriteLine("GpuModeController: PCI backend - live Eco→Standard applied (no reboot)");
                return GpuSwitchResult.Applied;
            }
            Logger.WriteLine("GpuModeController: PCI backend - live transition failed, falling back to deferred reboot");
        }

        // Schedule the change for the next reboot. WriteDriverBlock knows
        // how to handle both Eco (writes blocks) and non-Eco (clears blocks
        // and writes only the trigger) in PCI mode.
        SaveModeToConfig(target);
        WriteDriverBlock(target);

        if (File.Exists(TriggerPath))
        {
            Logger.WriteLine($"GpuModeController: PCI backend - scheduled {target} for next reboot");
            return GpuSwitchResult.RebootRequired;
        }

        // WriteDriverBlock did not produce a trigger file. Two reasons:
        //   1. Authentication was cancelled (pkexec dialog dismissed).
        //   2. The internal safety guard refused (Eco + MUX=0 latched).
        // In case (2) the earlier "WriteDriverBlock REFUSED" log line
        // explains why; the EcoBlocked result lets the UI surface a
        // friendlier reason than a generic failure toast.
        if (target == GpuMode.Eco && WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GpuModeController: PCI backend - Eco refused (MUX=0 latched, would cause black screen)");
            return GpuSwitchResult.EcoBlocked;
        }
        Logger.WriteLine("GpuModeController: PCI backend - trigger write failed (pkexec cancelled or write error)");
        return GpuSwitchResult.Failed;
    }

    // Atomic operations

    /// <summary>Always safe. Write dgpu_disable=0 (enable dGPU). Returns Applied.</summary>
    /// <summary>
    /// After the dGPU is re-enabled, give the driver a moment to settle then ask
    /// ModeControl to re-apply (or reset) the current mode's GPU tuning. Runs on a
    /// background task so it never blocks the switch.
    /// </summary>
    private static void ScheduleGpuTuningReapply()
    {
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                OnReapplyGpuTuning?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: GPU tuning reapply failed: {ex.Message}");
            }
        });
    }

    private GpuSwitchResult ExecuteEnableDgpu()
    {
        Logger.WriteLine("GpuModeController: enabling dGPU (dgpu_disable=0) - always safe");
        try
        {
            _wmi.SetGpuEco(false);
            // Switching away from Eco - remove block artifacts (dGPU driver should be loadable)
            RemoveDriverBlock();

            if (IsTestMode)
            {
                Logger.WriteLine("GpuModeController: test mode - skipping live dGPU hardware re-enable");
                return GpuSwitchResult.Applied;
            }

            TryPowerOnDgpuSlot();

            // Wait for the dGPU to actually re-appear on the PCI bus. The
            // dgpu_disable=0 write can be very slow on asus-armoury firmware
            // (20s+), and the single rescan in SetGpuEco often fires before the
            // device is electrically back, so it never re-enumerates. Poll for
            // the device, re-asserting slot power + rescan until it shows up. Gate
            // the nvidia daemon restart on the *device* (not the module - the
            // module can be present from a powerd respawn loop even with no GPU).
            bool present = WaitForDgpuDevice(15000);
            if (!present)
            {
                Logger.WriteLine("GpuModeController: dGPU did not re-appear after rescan - reboot likely required; skipping daemon restart");
                return GpuSwitchResult.DgpuReenableFailed;
            }

            bool isAmd = FindDgpuPciDevice()?.vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase) == true;
            if (isAmd)
            {
                // amdgpu also drives the iGPU, so it is never rmmod'd; after the
                // device re-enumerates, load it explicitly (udev coldplug is the
                // backup). Mirrors gpu-block-helper.sh live-standard.
                Logger.WriteLine("GpuModeController: AMD dGPU present - loading amdgpu");
                SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "modprobe", "amdgpu" }, sudoTimeoutMs: 10000);
            }
            else
            {
                Logger.WriteLine("GpuModeController: nvidia dGPU present - loading nvidia");
                SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "modprobe", "nvidia" }, sudoTimeoutMs: 10000);

                // Eco transition stopped these daemons; Standard must restart them
                // (supergfxctl actions.rs:enable_nvidia_persistenced + enable_nvidia_powerd).
                // Wait for kernel autoload of the nvidia module (needs /dev/nvidiactl).
                if (HasNvidiaDaemonsInstalled())
                {
                    if (WaitForNvidiaModule(5000))
                        RestartNvidiaDaemons();
                    else
                        Logger.WriteLine("GpuModeController: nvidia module did not load within 5s - skipping daemon restart");
                }
            }

            // Allow the dGPU to autosuspend (supergfxctl set_runtime_pm Auto).
            SetDgpuRuntimePmAuto();
            // Restore the NVIDIA Vulkan ICD now the dGPU is back.
            ApplyVulkanIcd(dgpuAvailable: true);
            Logger.WriteLine("GpuModeController: dGPU enabled");
            // Re-apply (or reset) the current mode's GPU tuning now the dGPU is
            // back, so persistence survives an Eco->Standard toggle.
            ScheduleGpuTuningReapply();
            return GpuSwitchResult.Applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: enable dGPU failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    /// <summary>
    /// Scan /sys/bus/pci/devices for the discrete GPU graphics function,
    /// regardless of whether a driver is bound: NVIDIA (vendor 0x10de) or AMD
    /// (vendor 0x1002 with boot_vga != 1 so the iGPU is excluded). Matches only
    /// VGA (0x0300xx) / 3D (0x0302xx) classes so audio/USB sibling functions are
    /// skipped. Returns (bdf, vendor) or null. Detects presence right after a
    /// rescan, before the driver binds.
    /// </summary>
    private static (string bdf, string vendor)? FindDgpuPciDevice()
    {
        try
        {
            string devDir = TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(devDir))
                return null;
            foreach (var dev in Directory.GetDirectories(devDir))
            {
                string vendorPath = Path.Combine(dev, "vendor");
                if (!File.Exists(vendorPath))
                    continue;
                string vendor = File.ReadAllText(vendorPath).Trim();
                bool isNvidia = vendor.Equals("0x10de", StringComparison.OrdinalIgnoreCase);
                bool isAmd = vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase);
                if (!isNvidia && !isAmd)
                    continue;

                string clsPath = Path.Combine(dev, "class");
                if (!File.Exists(clsPath))
                    continue;
                string cls = File.ReadAllText(clsPath).Trim();
                if (!cls.StartsWith("0x0300", StringComparison.Ordinal)
                    && !cls.StartsWith("0x0302", StringComparison.Ordinal))
                    continue; // not the graphics function (skip audio/USB siblings)

                if (isAmd)
                {
                    string bootVgaPath = Path.Combine(dev, "boot_vga");
                    if (File.Exists(bootVgaPath) && File.ReadAllText(bootVgaPath).Trim() == "1")
                        continue; // this is the AMD iGPU, not the dGPU
                }
                return (Path.GetFileName(dev), vendor);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: FindDgpuPciDevice failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Poll for the dGPU PCI device to re-appear after dgpu_disable=0, re-issuing
    /// /sys/bus/pci/rescan on each attempt (the firmware may need several seconds
    /// to electrically re-expose the device). Returns true once present.
    /// </summary>
    private static bool WaitForDgpuDevice(int timeoutMs)
    {
        int waited = 0;
        int attempt = 0;
        while (waited < timeoutMs)
        {
            if (FindDgpuPciDevice() != null)
            {
                Logger.WriteLine($"GpuModeController: dGPU present after {waited}ms ({attempt} rescan(s))");
                return true;
            }
            // Re-assert slot power (idempotent) then re-trigger enumeration;
            // SetGpuEco already did the first rescan.
            TryPowerOnDgpuSlot();
            SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            attempt++;
            Thread.Sleep(1000);
            waited += 1000;
        }
        return FindDgpuPciDevice() != null;
    }

    private const string DgpuSlotKey = "dgpu_pci_slot";
    public void CacheDgpuSlotIfPresent()
    {
        try
        { ResolveDgpuSlot(); }
        catch (Exception ex) { Logger.WriteLine($"GpuModeController: CacheDgpuSlotIfPresent failed: {ex.Message}"); }
    }

    private static string? ResolveDgpuSlot()
    {
        string? bdf = FindDgpuPciDevice()?.bdf;
        if (!string.IsNullOrEmpty(bdf))
        {
            string? slot = FindDgpuSlot(bdf!);
            if (!string.IsNullOrEmpty(slot))
            {
                if (AppConfig.GetString(DgpuSlotKey) != slot)
                {
                    AppConfig.Set(DgpuSlotKey, slot!);
                    Logger.WriteLine($"GpuModeController: cached dGPU PCIe slot {slot} (bdf {bdf})");
                }
                return slot;
            }
        }
        return AppConfig.GetString(DgpuSlotKey);
    }

    /// <summary>
    /// Find the PCIe slot whose address matches the dGPU BDF. Slot addresses are
    /// the function-less form (e.g. "0000:01:00"), so the dGPU BDF
    /// "0000:01:00.0" starts with it.
    /// </summary>
    private static string? FindDgpuSlot(string bdf)
    {
        try
        {
            string slotsDir = TestPathPrefix + "/sys/bus/pci/slots";
            if (!Directory.Exists(slotsDir))
                return null;
            foreach (var dir in Directory.GetDirectories(slotsDir))
            {
                string addrPath = Path.Combine(dir, "address");
                if (!File.Exists(addrPath))
                    continue;
                string addr = File.ReadAllText(addrPath).Trim();
                if (addr.Length > 0 && bdf.StartsWith(addr, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: FindDgpuSlot failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Write 1 to the dGPU PCIe slot's power file via gpu-helper (root-only), so
    /// the pciehp controller powers the slot and trains the link. Idempotent:
    /// skips when already powered, no-op (logs) when the slot is unknown.
    /// </summary>
    private static void TryPowerOnDgpuSlot()
    {
        string? slot = ResolveDgpuSlot();
        if (string.IsNullOrEmpty(slot))
        {
            Logger.WriteLine("GpuModeController: dGPU PCIe slot unknown - cannot assert slot power (rescan only)");
            return;
        }
        int cur = SysfsHelper.ReadInt(TestPathPrefix + $"/sys/bus/pci/slots/{slot}/power", -1);
        if (cur == 1)
            return;
        bool ok = RunSlotPower(slot!, "1");
        Logger.WriteLine($"GpuModeController: slot-power {slot} = 1 ({(ok ? "OK" : "FAILED")}) [was {cur}]");
    }

    private static bool RunSlotPower(string slot, string value)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "slot-power", slot, value },
            sudoTimeoutMs: 10000, pkexecTimeoutMs: 60000);
        return r != null;
    }

    private static bool HasNvidiaDaemonsInstalled()
        => File.Exists("/usr/lib/systemd/system/nvidia-powerd.service")
        || File.Exists("/etc/systemd/system/nvidia-powerd.service")
        || File.Exists("/lib/systemd/system/nvidia-powerd.service");

    private static bool WaitForNvidiaModule(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (Directory.Exists(TestPathPrefix + "/sys/module/nvidia"))
                return true;
            Thread.Sleep(100);
            waited += 100;
        }
        return false;
    }

    private static void RestartNvidiaDaemons()
    {
        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "reset-failed", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        var r1 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "start", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r1 != null
            ? "GpuModeController: started nvidia-persistenced"
            : "GpuModeController: nvidia-persistenced start failed (unit missing or rate-limited)");

        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "reset-failed", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        var r2 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "start", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r2 != null
            ? "GpuModeController: started nvidia-powerd"
            : "GpuModeController: nvidia-powerd start failed (unit missing or rate-limited)");
    }

    /// <summary>
    /// THE one dangerous operation. Checks driver safety first.
    /// If safe → writes dgpu_disable=1 (may block 30-60s).
    /// If unsafe → returns DriverBlocking.
    /// </summary>
    private GpuSwitchResult ExecuteDisableDgpu()
    {
        // Check if in Ultimate mode (MUX=0) - kernel refuses dgpu_disable=1
        int mux = GetEffectiveMux();
        if (mux == 0)
        {
            Logger.WriteLine("GpuModeController: MUX=0 (Ultimate) - cannot disable dGPU directly");
            // Latch MUX change - dgpu_disable must wait until MUX settles on next boot.
            // Do NOT write block here - MUX needs to settle first. ApplyPendingOnStartup()
            // will write the block after confirming MUX is correct.
            try
            {
                _wmi.SetGpuMuxMode(1);
                _pendingMuxLatch = 1;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: ExecuteDisableDgpu - MUX latch failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }


        if (IsDgpuDriverActive())
        {
            Logger.WriteLine("GpuModeController: dGPU driver is ACTIVE - returning DriverBlocking (user confirmation required)");
            LogHoldersSnapshot("DriverBlocking");
            return GpuSwitchResult.DriverBlocking;
        }

        // Safe to write
        Logger.WriteLine("GpuModeController: dGPU driver idle/absent - writing dgpu_disable=1");
        try
        {
            _wmi.SetGpuEco(true);

            // Verify the write took effect
            if (_wmi.GetGpuEco())
            {
                Logger.WriteLine("GpuModeController: dgpu_disable=1 confirmed");
                // Eco applied live - remove block artifacts (dgpu_disable=1 is persistent)
                RemoveDriverBlock();
                // Hide the NVIDIA Vulkan ICD while the dGPU is disabled.
                ApplyVulkanIcd(dgpuAvailable: false);
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GpuModeController: dgpu_disable=1 write did not take effect");
                return GpuSwitchResult.Failed;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: dgpu_disable=1 write failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    // Driver detection

    /// <summary>
    /// Check if the dGPU driver is currently active (holding the hardware).
    /// NVIDIA: check /sys/module/nvidia_drm/refcnt
    /// AMD: check dGPU PCI device power/runtime_status
    /// </summary>
    public bool IsDgpuDriverActive()
    {
        if (IsNvidiaGpu())
            return IsNvidiaDriverActive();

        if (IsAmdDgpu())
            return IsAmdDriverActive();

        // No known dGPU driver loaded - safe
        Logger.WriteLine("GpuModeController: no dGPU driver detected - safe");
        return false;
    }

    private bool IsNvidiaDriverActive()
    {
        // The full nvidia kernel module family. nvidia_drm is the display
        // path; nvidia_uvm is the CUDA/compute path; nvidia_modeset wires
        // KMS. Any one of them in use is enough to keep nvidia loaded
        // and Eco unable to write dgpu_disable cleanly.
        string[] modules = new[] { "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia" };
        bool anyModuleLoaded = false;
        foreach (var mod in modules)
        {
            string modDir = TestPathPrefix + "/sys/module/" + mod;
            if (!Directory.Exists(modDir))
                continue;
            anyModuleLoaded = true;

            int refcnt = SysfsHelper.ReadInt(modDir + "/refcnt", -1);
            if (refcnt < 0)
            {
                Logger.WriteLine($"GpuModeController: {mod} loaded but refcnt unreadable - assuming ACTIVE");
                return true;
            }
            if (refcnt > 0)
            {
                Logger.WriteLine($"GpuModeController: {mod} refcnt={refcnt} - driver ACTIVE");
                return true;
            }
        }

        if (!anyModuleLoaded)
        {
            Logger.WriteLine("GpuModeController: no nvidia* modules loaded - safe");
            return false;
        }

        // Modules loaded but all refcnts are zero. Defense in depth: any
        // process holding /dev/nvidia* FDs OR mapping libnvidia/libcuda
        // counts as "active" so the user sees the blocking dialog before
        // we touch the kernel modules. Lib-mappers (rustdesk, kwin,
        // plasmashell) don't strictly block rmmod, but unloading the
        // driver under them risks silent failures or session crashes -
        // the dialog gives the user explicit control.
        int totalHolders = NvidiaProcessScanner.CountHolders();
        if (totalHolders > 0)
        {
            int fdHolders = NvidiaProcessScanner.CountFdHolders();
            Logger.WriteLine($"GpuModeController: {totalHolders} holders ({fdHolders} active FD, {totalHolders - fdHolders} libnvidia-mapped) - driver ACTIVE");
            return true;
        }

        Logger.WriteLine("GpuModeController: all nvidia* modules idle, no holders - driver safe");
        return false;
    }

    private bool IsAmdDriverActive()
    {
        string? pciAddr = FindDgpuPciAddress();
        if (pciAddr == null)
        {
            Logger.WriteLine("GpuModeController: AMD dGPU PCI address not found - assuming safe");
            return false;
        }

        string status = ReadDgpuRuntimeStatus(pciAddr);
        if (status == "suspended")
        {
            Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} runtime_status=suspended - safe");
            return false;
        }

        Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} runtime_status={status} - ACTIVE");
        return true;
    }

    // Driver release

    /// <summary>
    /// Attempt to release the dGPU driver so dgpu_disable=1 can proceed safely.
    /// NVIDIA: pkexec rmmod nvidia stack
    /// AMD: pkexec PCI unbind+remove
    /// Returns true if driver was released.
    /// </summary>
    private bool TryReleaseGpuDriver()
    {
        if (IsNvidiaGpu())
            return TryReleaseNvidiaDriver();

        if (IsAmdDgpu())
            return TryReleaseAmdDriver();

        return true; // No driver to release
    }

    private bool TryReleaseNvidiaDriver()
    {
        Logger.WriteLine("GpuModeController: attempting NVIDIA driver release");

        // CRITICAL: undo any GPU/VRAM clock lock and clock offsets BEFORE powering
        // the dGPU off. Locked clocks (nvidia-smi -lgc/-lmc) pin the GPU's power
        // management on, so it can never enter the D3cold state that dgpu_disable=1
        // needs to power-gate it. Leaving a lock set makes the Eco write stall ~25s
        // (ACPI/EC timeout) and the dGPU then fails to re-enumerate on the next
        // rescan - a hard wedge that only a reboot clears. Must run while the
        // driver is still loaded.
        ResetDgpuToStock();

        // Cache the dGPU's PCIe slot while the device is still present, so the
        // Standard re-enable can re-power it even though it will be gone by then.
        ResolveDgpuSlot();

        var r1 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r1 != null ? "GpuModeController: stopped nvidia-powerd" : "GpuModeController: nvidia-powerd stop failed");
        var r2 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r2 != null ? "GpuModeController: stopped nvidia-persistenced" : "GpuModeController: nvidia-persistenced stop failed");
        Thread.Sleep(500);

        return ReleaseNvidiaModulesAndPurgeHolders(FindNvidiaPciAddress(), out _);
    }

    /// <summary>
    /// Return the dGPU to stock clocks (unlock GPU + VRAM clocks, zero core/mem
    /// offsets) before it is powered off. Best-effort with short timeouts so it
    /// never adds delay when the GPU is already unresponsive. See
    /// <see cref="TryReleaseNvidiaDriver"/> for why this is required.
    /// </summary>
    private static void ResetDgpuToStock()
    {
        try
        {
            string helper = SysfsHelper.GpuHelperPath;
            // Unlock GPU and VRAM clocks - the part that blocks D3cold.
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "smi", "-rgc" }, sudoTimeoutMs: 4000);
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "smi", "-rmc" }, sudoTimeoutMs: 4000);
            // Zero any core/mem clock offsets (modern per-pstate API in gpu-helper).
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "nvml-clocks", "0", "0" }, sudoTimeoutMs: 4000);
            Logger.WriteLine("GpuModeController: reset dGPU clocks to stock before power-off");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: ResetDgpuToStock failed (non-fatal): {ex.Message}");
        }
    }

    private static readonly string[] NvidiaModules =
        { "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia", "nvidia_wmi_ec_backlight" };

    // Mirror of supergfxctl pci_device.rs:673 (iter.rev() unbind before power change).
    private record UnbindRecord(string Bdf, string DriverName);

    private static bool ReleaseNvidiaModulesAndPurgeHolders(string? dgpuBdf, out List<UnbindRecord> unbindStack)
    {
        unbindStack = new List<UnbindRecord>();

        if (!string.IsNullOrEmpty(dgpuBdf))
        {
            var funcs = EnumerateDgpuFunctions(dgpuBdf!);
            funcs.Reverse(); // highest function first (.1 audio before .0 graphics)
            foreach (var rec in funcs)
            {
                Logger.WriteLine($"GpuModeController: unbinding {rec.Bdf} from {rec.DriverName}");
                if (!TryUnbindFunction(rec))
                {
                    Logger.WriteLine($"GpuModeController: unbind {rec.Bdf} FAILED - rolling back");
                    RollbackUnbinds(unbindStack);
                    unbindStack = new List<UnbindRecord>();
                    return false;
                }
                unbindStack.Add(rec);
            }
            if (unbindStack.Count > 0)
                Thread.Sleep(100); // settle after unbinds
        }
        else
        {
            Logger.WriteLine("GpuModeController: dGPU BDF not resolvable - skipping sibling unbind step");
        }

        foreach (var m in NvidiaModules)
            RmmodOneModule(m);

        NvidiaProcessScanner.KillAllHolders(force: true, out int killed, out int failed);
        Logger.WriteLine($"GpuModeController: scorched earth killed={killed} failed={failed}");

        // nvidia module sometimes survives the cascade (refcnt=0 orphan).
        // Aggressive retry with longer settle before declaring failure.
        if (Directory.Exists(TestPathPrefix + "/sys/module/nvidia"))
        {
            int orphanRefcnt = SysfsHelper.ReadInt(TestPathPrefix + "/sys/module/nvidia/refcnt", -1);
            Logger.WriteLine($"GpuModeController: WARNING nvidia module orphaned refcnt={orphanRefcnt} - aggressive retry");
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(200);
                var (rc, err) = RunRmmod("nvidia");
                if (rc == 0)
                {
                    Logger.WriteLine($"GpuModeController: nvidia orphan unloaded after {i + 1} retries");
                    break;
                }
                if (err.Contains("is not currently loaded", StringComparison.Ordinal))
                {
                    Logger.WriteLine("GpuModeController: nvidia orphan cleared by external action");
                    break;
                }
                Logger.WriteLine($"GpuModeController: nvidia orphan retry {i + 1}/5: {err.Trim()}");
            }
        }

        bool drmGone = !Directory.Exists(TestPathPrefix + "/sys/module/nvidia_drm");
        bool nvidiaGone = !Directory.Exists(TestPathPrefix + "/sys/module/nvidia");
        bool gone = drmGone && nvidiaGone;
        Logger.WriteLine($"GpuModeController: nvidia_drm {(drmGone ? "unloaded" : "still loaded")}, nvidia {(nvidiaGone ? "unloaded" : "still loaded")}");
        if (!gone)
        {
            Logger.WriteLine("GpuModeController: modules still loaded after release - rolling back unbinds");
            RollbackUnbinds(unbindStack);
            unbindStack = new List<UnbindRecord>();
        }
        return gone;
    }

    private static List<UnbindRecord> EnumerateDgpuFunctions(string dgpuBdf)
    {
        // dgpuBdf = "0000:01:00.0" -> prefix "0000:01:00"
        int dotIx = dgpuBdf.LastIndexOf('.');
        if (dotIx < 0)
            return new List<UnbindRecord>();
        string prefix = dgpuBdf.Substring(0, dotIx);

        string root = TestPathPrefix + "/sys/bus/pci/devices/";
        var results = new List<UnbindRecord>();
        if (!Directory.Exists(root))
            return results;

        foreach (var dir in Directory.GetDirectories(root))
        {
            string bdf = Path.GetFileName(dir);
            if (!bdf.StartsWith(prefix + ".", StringComparison.Ordinal))
                continue;

            string driverLink = Path.Combine(dir, "driver");
            if (!Directory.Exists(driverLink))
            {
                Logger.WriteLine($"GpuModeController: {bdf} no driver bound, skipping");
                continue;
            }
            string? driverName = null;
            try
            { driverName = Path.GetFileName(new DirectoryInfo(driverLink).ResolveLinkTarget(true)?.FullName ?? ""); }
            catch { }
            if (string.IsNullOrEmpty(driverName))
            {
                Logger.WriteLine($"GpuModeController: {bdf} could not resolve driver symlink, skipping");
                continue;
            }
            results.Add(new UnbindRecord(bdf, driverName));
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Bdf, b.Bdf));
        return results;
    }

    /// <summary>
    /// All PCI function nodes of the dGPU (e.g. 0000:01:00.0/.1/.2/.3),
    /// regardless of whether a driver is bound. Used to apply runtime-PM
    /// (power/control) to every function after a Standard re-enable.
    /// </summary>
    private static List<string> EnumerateDgpuDeviceNodes(string dgpuBdf)
    {
        var results = new List<string>();
        int dotIx = dgpuBdf.LastIndexOf('.');
        string prefix = dotIx < 0 ? dgpuBdf : dgpuBdf.Substring(0, dotIx);
        string root = TestPathPrefix + "/sys/bus/pci/devices/";
        if (!Directory.Exists(root))
            return results;
        foreach (var dir in Directory.GetDirectories(root))
        {
            string bdf = Path.GetFileName(dir);
            if (bdf.StartsWith(prefix + ".", StringComparison.Ordinal))
                results.Add(bdf);
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static bool TryUnbindFunction(UnbindRecord rec)
        => RunPciAction("pci-unbind", rec.DriverName, rec.Bdf);

    private static bool TryRebindFunction(UnbindRecord rec)
        => RunPciAction("pci-bind", rec.DriverName, rec.Bdf);

    private static void RollbackUnbinds(List<UnbindRecord> stack)
    {
        if (stack.Count == 0)
            return;
        // Rebind in reverse: graphics (.0) before audio (.1) so audio power gating works.
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var rec = stack[i];
            string driverPath = TestPathPrefix + $"/sys/bus/pci/drivers/{rec.DriverName}";
            if (!Directory.Exists(driverPath))
            {
                Logger.WriteLine($"GpuModeController: rollback skip {rec.Bdf} - driver {rec.DriverName} no longer registered (reboot to recover)");
                continue;
            }
            bool ok = TryRebindFunction(rec);
            Logger.WriteLine($"GpuModeController: rollback rebind {rec.Bdf} -> {rec.DriverName} = {(ok ? "OK" : "FAILED")}");
        }
    }

    private static bool RunPciAction(string action, string driver, string bdf)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { action, driver, bdf },
            sudoTimeoutMs: 10000, pkexecTimeoutMs: 60000);
        return r != null;
    }

    private static bool RunPciRemove(string bdf)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "pci-remove", bdf },
            sudoTimeoutMs: 10000, pkexecTimeoutMs: 60000);
        return r != null;
    }

    private static bool RunPciPower(string bdf, string value)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "pci-power", bdf, value },
            sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
        return r != null;
    }

    /// <summary>
    /// Set power/control=auto on every dGPU PCI function after Standard
    /// re-enable so the device can runtime-suspend when idle (supergfxctl
    /// set_runtime_pm Auto). Best-effort - never fails the switch.
    /// </summary>
    private static void SetDgpuRuntimePmAuto()
    {
        string? bdf = FindDgpuPciDevice()?.bdf;
        if (string.IsNullOrEmpty(bdf))
            return;
        foreach (var node in EnumerateDgpuDeviceNodes(bdf!))
        {
            if (File.Exists(TestPathPrefix + $"/sys/bus/pci/devices/{node}/power/control"))
                RunPciPower(node, "auto");
        }
    }

    private const string NvidiaVulkanIcd = "/usr/share/vulkan/icd.d/nvidia_icd.json";

    /// <summary>
    /// Hide (Eco) / show (Standard) the NVIDIA Vulkan ICD via gpu-helper so
    /// Vulkan apps fall back cleanly to the iGPU when the dGPU is disabled
    /// (supergfxctl check_vulkan_icd). Skips the privileged call entirely on
    /// systems with no NVIDIA Vulkan ICD. Best-effort - never fails the switch.
    /// </summary>
    private static void ApplyVulkanIcd(bool dgpuAvailable)
    {
        if (!File.Exists(NvidiaVulkanIcd) && !File.Exists(NvidiaVulkanIcd + "_inactive"))
            return;
        SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "vulkan-icd", dgpuAvailable ? "show" : "hide" },
            sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
    }

    private static void RmmodOneModule(string module)
    {
        const int maxTries = 7;
        for (int i = 0; i < maxTries; i++)
        {
            var (exitCode, stderr) = RunRmmod(module);
            if (exitCode == 0)
            {
                Logger.WriteLine($"GpuModeController: rmmod {module} OK");
                return;
            }

            if (stderr.EndsWith("is not currently loaded\n", StringComparison.Ordinal)
                || stderr.EndsWith("is not currently loaded", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GpuModeController: {module} not loaded, skipping");
                return;
            }
            if (stderr.EndsWith("is builtin.\n", StringComparison.Ordinal)
                || stderr.EndsWith("is builtin.", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GpuModeController: {module} is builtin, cannot remove");
                return;
            }
            if (stderr.EndsWith("Permission denied\n", StringComparison.Ordinal)
                || stderr.EndsWith("Permission denied", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GpuModeController: rmmod {module} permission denied: {stderr.Trim()}");
                return;
            }
            if (stderr.Contains($"Module {module} not found", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GpuModeController: module {module} not found");
                return;
            }

            if (i == maxTries - 1)
            {
                Logger.WriteLine($"GpuModeController: rmmod {module} failed after {maxTries} tries: {stderr.Trim()}");
                return;
            }
            Thread.Sleep(50);
        }
    }

    private static (int exitCode, string stderr) RunRmmod(string module)
    {
        try
        {
            // Route through the root helper (helper execv's rmmod, so its
            // stderr/exit propagate verbatim and the parsing below still works).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = SysfsHelper.SudoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(SysfsHelper.GpuHelperPath);
            psi.ArgumentList.Add("rmmod");
            psi.ArgumentList.Add(module);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return (-1, "");
            if (!proc.WaitForExit(5000))
            {
                try
                { proc.Kill(); }
                catch { }
                return (-1, "timeout");
            }
            string stderr = proc.StandardError.ReadToEnd();
            return (proc.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static void LogHoldersSnapshot(string reason)
    {
        try
        {
            var holders = NvidiaProcessScanner.ScanHolders();
            int refcnt = SysfsHelper.ReadInt("/sys/module/nvidia/refcnt", -1);
            if (holders.Count == 0)
            {
                Logger.WriteLine($"GpuModeController: {reason} holders=0 nvidia/refcnt={refcnt}");
                return;
            }
            var parts = new List<string>(holders.Count);
            foreach (var h in holders)
                parts.Add($"{h.Pid}:{h.Comm}({h.User})/{h.FdCount}fds");
            Logger.WriteLine($"GpuModeController: {reason} holders={holders.Count} nvidia/refcnt={refcnt} [{string.Join(", ", parts)}]");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: LogHoldersSnapshot failed: {ex.Message}");
        }
    }

    private bool TryReleaseAmdDriver()
    {
        string? pciAddr = FindDgpuPciAddress();
        if (pciAddr == null)
        {
            Logger.WriteLine("GpuModeController: AMD dGPU PCI address not found");
            return false;
        }

        Logger.WriteLine($"GpuModeController: attempting AMD dGPU PCI unbind+remove for {pciAddr}");

        // Cache the PCIe slot while the device is still present (Standard
        // re-enable re-powers it). amdgpu is NEVER rmmod'd - it also drives the
        // iGPU/display; instead each dGPU PCI function is unbound from its driver
        // and then removed from the bus (supergfxctl Device::remove()).
        ResolveDgpuSlot();

        var funcs = EnumerateDgpuFunctions(pciAddr);
        funcs.Reverse(); // highest function first (.1 audio before .0 graphics)
        foreach (var rec in funcs)
        {
            Logger.WriteLine($"GpuModeController: unbinding {rec.Bdf} from {rec.DriverName}");
            RunPciAction("pci-unbind", rec.DriverName, rec.Bdf);
            Logger.WriteLine($"GpuModeController: removing {rec.Bdf} from PCI bus");
            RunPciRemove(rec.Bdf);
        }

        // Verify: the graphics function should be gone (removed) or at least
        // have no driver bound.
        string driverLink = $"/sys/bus/pci/devices/{pciAddr}/driver";
        bool deviceGone = !Directory.Exists($"/sys/bus/pci/devices/{pciAddr}");
        if (deviceGone || (!File.Exists(driverLink) && !Directory.Exists(driverLink)))
        {
            Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} released ({(deviceGone ? "removed" : "unbound")})");
            return true;
        }

        Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} release may have failed");
        return false;
    }

    // Hardware detection helpers

    /// <summary>True if the NVIDIA kernel module is loaded.</summary>
    private static bool IsNvidiaGpu()
    {
        return Directory.Exists(TestPathPrefix + "/sys/module/nvidia");
    }

    /// <summary>True if an AMD discrete GPU is present (vendor=0x1002, boot_vga=0).</summary>
    private bool IsAmdDgpu()
    {
        return FindDgpuPciAddress() != null;
    }

    /// <summary>Read /sys/module/nvidia_drm/refcnt. Returns -1 if not readable.</summary>
    private static int ReadNvidiaDrmRefcount()
    {
        return SysfsHelper.ReadInt(TestPathPrefix + "/sys/module/nvidia_drm/refcnt", -1);
    }

    /// <summary>Read power/runtime_status for a PCI device. Returns "active"/"suspended"/etc.</summary>
    private static string ReadDgpuRuntimeStatus(string pciAddr)
    {
        string path = TestPathPrefix + $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        return SysfsHelper.ReadAttribute(path) ?? "active";
    }

    /// <summary>
    /// Scan /sys/bus/pci/devices for the AMD discrete GPU.
    /// Looks for vendor=0x1002, class=0x0300xx or 0x0302xx, boot_vga=0.
    /// Caches the result.
    /// </summary>
    private string? FindDgpuPciAddress()
    {
        if (_dgpuPciScanned)
            return _cachedDgpuPciAddress;
        _dgpuPciScanned = true;

        try
        {
            string pciDir = "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir))
                return null;

            foreach (var deviceDir in Directory.GetDirectories(pciDir))
            {
                string? vendor = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "vendor"));
                if (vendor != "0x1002")
                    continue; // Not AMD

                string? cls = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "class"));
                if (cls == null)
                    continue;
                // VGA: 0x030000, 3D controller: 0x030200
                if (!cls.StartsWith("0x0300") && !cls.StartsWith("0x0302"))
                    continue;

                string? bootVga = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "boot_vga"));
                if (bootVga == "1")
                    continue; // This is the iGPU, skip

                // Confirm it has a DRM subsystem
                if (!Directory.Exists(Path.Combine(deviceDir, "drm")))
                    continue;

                _cachedDgpuPciAddress = Path.GetFileName(deviceDir);
                Logger.WriteLine($"GpuModeController: found AMD dGPU at {_cachedDgpuPciAddress}");
                return _cachedDgpuPciAddress;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: PCI scan failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Find NVIDIA dGPU PCI address by walking the nvidia driver's bound devices.
    /// Returns null if nvidia is unbound (e.g., dGPU in Eco mode).
    /// </summary>
    private static string? FindNvidiaPciAddress()
    {
        try
        {
            string driverDir = "/sys/bus/pci/drivers/nvidia";
            if (!Directory.Exists(driverDir))
                return null;

            foreach (var item in Directory.GetFileSystemEntries(driverDir))
            {
                string name = Path.GetFileName(item);
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-9]$"))
                    return name;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: FindNvidiaPciAddress failed: {ex.Message}");
        }
        return null;
    }

    // Boot safety (supergfxctl pattern)

    /// <summary>
    /// Detect and fix the impossible state: gpu_mux_mode=0 (Ultimate) + dgpu_disable=1.
    /// This causes boot hangs because the dGPU is the sole display output in MUX=0
    /// but it's been powered off. Force dgpu_disable=0 to recover.
    ///
    /// Also removes any stale block artifacts that could prevent the dGPU driver from loading.
    ///
    /// Inspired by supergfxctl's asus_boot_safety_check().
    /// </summary>
    private void BootSafetyCheck()
    {
        try
        {
            if (AppConfig.NoGpu() || AppConfig.IsAMDiGPU())
                return;

            // PCI backend has no MUX hardware and no live dgpu_disable, so
            // the impossible-state pair this check defends against simply
            // cannot occur. Skip silently to avoid touching WMI sysfs that
            // may not exist on non-ASUS systems.
            if (AppConfig.IsPciGpuBackend())
                return;

            int mux = _wmi.GetGpuMuxMode();
            bool ecoEnabled = _wmi.GetGpuEco();

            if (mux == 0 && ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: BOOT SAFETY - MUX=0 + dgpu_disable=1 is impossible!");
                Logger.WriteLine("GpuModeController: BOOT SAFETY - forcing dgpu_disable=0 to recover");
                _wmi.SetGpuEco(false);
                // Remove block artifacts to allow dGPU driver to load after recovery
                RemoveDriverBlock();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: BootSafetyCheck failed: {ex.Message}");
        }
    }

    // Driver block - prevent dGPU driver loading + remove PCI devices for Eco boot

    /// <summary>
    /// Optional root prefix for the four ghelper system paths. Empty in
    /// production (paths resolve under real /etc); the test harness sets
    /// <c>GHELPER_TEST_ROOT=/tmp/scenario-N</c> so writes are confined to a
    /// sandbox and the sudo / pkexec branches can be skipped.
    /// Mirrors the same env var the boot-script test harness uses.
    /// </summary>
    internal static readonly string TestPathPrefix =
        Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT") ?? "";

    internal static bool IsTestMode => !string.IsNullOrEmpty(TestPathPrefix);

    /// <summary>Path to modprobe.d file that blocks dGPU driver loading (NVIDIA + AMD).</summary>
    internal static readonly string ModprobeBlockPath = TestPathPrefix + "/etc/modprobe.d/ghelper-gpu-block.conf";

    /// <summary>Path to udev rule that removes dGPU PCI devices from the bus (NVIDIA + AMD).</summary>
    internal static readonly string UdevRemovePath = TestPathPrefix + "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules";

    /// <summary>Path to trigger file read by ghelper on startup.</summary>
    internal static readonly string TriggerPath = TestPathPrefix + "/etc/ghelper/pending-gpu-mode";

    /// <summary>Path to backend selector file. Content "asus-wmi" or "pci".</summary>
    internal static readonly string BackendPath = TestPathPrefix + "/etc/ghelper/backend";

    /// <summary>
    /// In-process replacement for the sudo/pkexec helper calls when running
    /// under the C# test harness. Performs the exact same file-system effect
    /// the helper script would, but without any privilege escalation.
    /// Mirrors gpu-block-helper.sh write/clean/set-backend semantics.
    /// </summary>
    private static void RunHelperInTestMode(string action, GpuMode? target = null, string? backend = null)
    {
        try
        {
            string? mkdir(string p)
            { var d = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d); return d; }
            switch (action)
            {
                case "write":
                    string modeStr = (target ?? GpuMode.Eco) switch
                    {
                        GpuMode.Eco => "eco",
                        GpuMode.Standard => "standard",
                        GpuMode.Optimized => "optimized",
                        GpuMode.Ultimate => "ultimate",
                        _ => "eco",
                    };
                    string be = backend ?? "asus-wmi";
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, be);
                    if (modeStr == "eco")
                    {
                        mkdir(ModprobeBlockPath);
                        File.WriteAllText(ModprobeBlockPath, ModprobeBlockContent);
                        mkdir(UdevRemovePath);
                        File.WriteAllText(UdevRemovePath, UdevRemoveContent);
                    }
                    else
                    {
                        if (File.Exists(ModprobeBlockPath))
                            File.Delete(ModprobeBlockPath);
                        if (File.Exists(UdevRemovePath))
                            File.Delete(UdevRemovePath);
                    }
                    mkdir(TriggerPath);
                    File.WriteAllText(TriggerPath, modeStr);
                    break;
                case "clean":
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    break;
                case "set-backend":
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, backend ?? "asus-wmi");
                    break;
                case "live-standard":
                    // Mirror the bash helper: remove blocks + trigger.
                    // udevadm + PCI rescan + modprobe are out-of-scope
                    // for a userland test sandbox (no real kernel).
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"RunHelperInTestMode({action}) failed: {ex.Message}");
        }
    }

    /// <summary>Known locations for the GPU block helper script (installed by install-local.sh).</summary>
    private static readonly string[] HelperSearchPaths = new[]
    {
        "/usr/local/lib/ghelper/gpu-block-helper.sh",
        "/opt/ghelper/gpu-block-helper.sh",
    };

    /// <summary>Cached path to the helper script. Null if not found.</summary>
    private static string? _cachedHelperPath;
    private static bool _helperPathScanned;

    /// <summary>
    /// Find the GPU block helper script. Checked once and cached.
    /// Returns null if not found (falls back to pkexec).
    /// </summary>
    private static string? FindHelperScript()
    {
        if (_helperPathScanned)
            return _cachedHelperPath;
        _helperPathScanned = true;

        foreach (var path in HelperSearchPaths)
        {
            if (File.Exists(path))
            {
                _cachedHelperPath = path;
                Logger.WriteLine($"GpuModeController: GPU block helper found at {path}");
                return path;
            }
        }

        Logger.WriteLine("GpuModeController: GPU block helper not found - will use pkexec fallback");
        return null;
    }

    /// <summary>Content for the modprobe.d block file (vendor-aware: NVIDIA + AMD).</summary>
    private const string ModprobeBlockContent =
        "# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Uses 'install /bin/false' (strongest block - prevents loading by ANY means)\n" +
        "# NVIDIA modules\n" +
        "install nvidia /bin/false\n" +
        "install nvidia_drm /bin/false\n" +
        "install nvidia_modeset /bin/false\n" +
        "install nvidia_uvm /bin/false\n" +
        "install nvidia_wmi_ec_backlight /bin/false\n" +
        "# Open-source NVIDIA driver\n" +
        "install nouveau /bin/false\n" +
        "# AMD dGPU driver\n" +
        "install amdgpu /bin/false\n";

    /// <summary>Content for the udev rule that PCI-removes dGPU devices (NVIDIA + AMD) on add.</summary>
    private const string UdevRemoveContent =
        "# ghelper: remove dGPU PCI devices so no driver can bind\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Remove NVIDIA VGA controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030200\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA Audio devices (HDMI audio on dGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB xHCI Host Controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c0330\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB Type-C UCSI devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c8000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030000\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030200\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU Audio devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n";

    /// <summary>
    /// Push the backend selector marker (/etc/ghelper/backend) without
    /// touching any block artifacts. Called when the user toggles the
    /// "Use PCI dGPU disable" checkbox so the boot service sees the new
    /// backend on the next boot even if the user never schedules a mode
    /// change. Idempotent - no-op if the marker already matches.
    /// </summary>
    public void PushBackendMarker(string backend)
    {
        if (backend != "pci" && backend != "asus-wmi")
        {
            Logger.WriteLine($"GpuModeController: PushBackendMarker rejected invalid backend '{backend}'");
            return;
        }

        // The MUX=0 latch flag and in-memory pending latch encode REAL
        // firmware state (gpu_mux_mode=0 is pending an actual reboot). The
        // backend selector is a UI preference - toggling it does not change
        // firmware state. Clearing the latch here would re-introduce the
        // exact impossible-state chain this guard is designed to prevent:
        // user clicks Ultimate -> toggles PCI -> clicks Eco -> blocks
        // written -> reboot -> MUX=0 settles + udev removes dGPU = black
        // screen. Keep the latch; rely on WouldCreateImpossibleState to
        // refuse the subsequent Eco click and surface the new toast.

        try
        {
            // Skip the privileged call when the file already reflects the
            // chosen backend. Saves a polkit prompt on every checkbox tick.
            if (File.Exists(BackendPath))
            {
                string current = File.ReadAllText(BackendPath).Trim();
                if (current == backend)
                    return;
            }

            if (IsTestMode)
            {
                RunHelperInTestMode("set-backend", backend: backend);
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Match the WriteDriverBlock invocation style - no nested
                // quoting, no `sh -c`. The helper has a dedicated
                // `set-backend` subcommand that writes only the marker.
                Logger.WriteLine($"GpuModeController: PushBackendMarker via helper: {backend}");
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "set-backend", backend }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
            }
            else
            {
                // pkexec fallback uses RunPkexecBash so the script body is
                // passed as a single argument (no quoting hazards).
                Logger.WriteLine($"GpuModeController: PushBackendMarker via pkexec: {backend}");
                SysfsHelper.RunPkexecBash(
                    $"mkdir -p /etc/ghelper\necho {backend} > {BackendPath}\nchmod 644 {BackendPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: PushBackendMarker failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Live PCI Eco → Standard recovery: atomically remove the modprobe
    /// block + udev hot-remove rule + trigger file, reload udev so the
    /// rule is forgotten, rescan the PCI bus to re-enumerate the dGPU,
    /// and kick modprobe explicitly to bring the driver back. No reboot
    /// required. Mirrors the asus-wmi SetGpuEco(false) live path.
    ///
    /// Returns Applied on success (verified by block files being gone),
    /// Failed otherwise. Callers fall through to the deferred-reboot path
    /// when this returns Failed so the user can still escape Eco mode.
    /// </summary>
    private GpuSwitchResult TryLiveRemovePciBlocks()
    {
        try
        {
            if (IsTestMode)
            {
                // Honour the test-harness failure switch so the
                // C# scenario suite can exercise the fall-through path.
                if (Environment.GetEnvironmentVariable("GHELPER_TEST_FAIL_LIVE_STANDARD") == "1")
                {
                    Logger.WriteLine("TryLiveRemovePciBlocks: test harness requested failure");
                    return GpuSwitchResult.Failed;
                }
                RunHelperInTestMode("live-standard");
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GpuModeController: live Eco→Standard via helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "live-standard" }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
                }
                else
                {
                    Logger.WriteLine("GpuModeController: live Eco→Standard via pkexec fallback");
                    // RunPkexecBash passes the script body as a single
                    // ArgumentList entry, so the interpolated path
                    // constants below cannot escape into argv parsing.
                    // The paths themselves are private const strings -
                    // not user input.
                    string script =
                        $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}\n" +
                        "udevadm control --reload-rules 2>/dev/null || true\n" +
                        "echo 1 > /sys/bus/pci/rescan 2>/dev/null || true\n" +
                        "modprobe nvidia 2>/dev/null || true\n" +
                        "modprobe amdgpu 2>/dev/null || true";
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            // Verify the recovery actually happened. The helper script
            // returns 0 even when individual operations fail (the trailing
            // `|| true`); the only reliable signal is file-state.
            bool stillBlocked = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);
            if (stillBlocked)
                return GpuSwitchResult.Failed;

            OnLivePciTransition?.Invoke();
            return GpuSwitchResult.Applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TryLiveRemovePciBlocks failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    /// <summary>
    /// True if any driver block artifacts (current or legacy) exist on disk.
    /// Used to decide if cleanup is needed - avoids unnecessary pkexec prompts.
    /// BackendPath is intentionally excluded - it is a persistent user
    /// preference managed independently of Eco state.
    /// </summary>
    private static bool DriverBlockExists()
    {
        return File.Exists(ModprobeBlockPath)
            || File.Exists(UdevRemovePath)
            || File.Exists(TriggerPath);
    }

    /// <summary>
    /// Write three artifacts that prevent dGPU drivers from loading on the next boot,
    /// allowing ghelper to safely write dgpu_disable=1 at startup.
    ///
    ///
    /// 1. modprobe.d `install /bin/false` - the STRONGEST modprobe block.
    ///    Unlike `blacklist` (which only prevents autoload and can be overridden
    ///    by dependencies), `install /bin/false` replaces `modprobe nvidia` with
    ///    a no-op. Blocks both NVIDIA and AMD dGPU modules.
    ///
    /// 2. udev rule `ATTR{remove}="1"` - belt and suspenders.
    ///    Physically removes all dGPU PCI devices from the bus when they appear.
    ///    Even if the modprobe block somehow fails (e.g. nvidia in initramfs),
    ///    there's no PCI device for the driver to bind to.
    ///
    /// 3. Trigger file `/etc/ghelper/pending-gpu-mode` - tells ghelper on
    ///    startup to write dgpu_disable=1 and clean up.
    ///
    /// Prefers the sudo helper script (installed by install-local.sh) which needs
    /// no tty/polkit and works from autostart. Falls back to pkexec if the helper
    /// is not installed.
    ///
    /// Only Eco needs the block. All other modes want the dGPU driver available.
    /// </summary>
    private void WriteDriverBlock(GpuMode target)
    {
        try
        {
            bool isPci = AppConfig.IsPciGpuBackend();
            bool isEco = (target == GpuMode.Eco);

            if (!isEco && !isPci)
            {
                // ASUS WMI backend, non-eco target: dGPU driver should be
                // available immediately, no block artifacts needed.
                RemoveDriverBlock();
                return;
            }

            // SAFETY: Never write Eco block artifacts when MUX is latched to 0 (Ultimate).
            // After reboot, MUX=0 means dGPU is the sole display - blocking dGPU driver and
            // writing dgpu_disable=1 would cause a black screen (impossible state).
            // WouldCreateImpossibleState only triggers on ASUS hardware with a real MUX;
            // PCI backend on non-ASUS naturally returns false.
            if (isEco && WouldCreateImpossibleState(target))
            {
                Logger.WriteLine("GpuModeController: WriteDriverBlock REFUSED - Eco + MUX=0 is impossible state, removing any stale artifacts instead");
                RemoveDriverBlock();
                return;
            }

            // Convert target mode to string for trigger file (boot script reads this)
            string modeStr = target switch
            {
                GpuMode.Eco => "eco",
                GpuMode.Standard => "standard",
                GpuMode.Optimized => "optimized",
                GpuMode.Ultimate => "ultimate",
                _ => "eco"
            };

            // Pass the active backend through to the helper so it writes the
            // marker file the boot script reads. Default "asus-wmi" preserves
            // legacy behaviour for any caller that hasn't opted into PCI mode.
            string backend = AppConfig.GetGpuBackend();

            if (isEco)
                Logger.WriteLine($"GpuModeController: writing driver block (modprobe + udev + trigger=eco, backend={backend})");
            else
                Logger.WriteLine($"GpuModeController: PCI backend - writing trigger={modeStr} (no blocks, backend={backend})");

            if (IsTestMode)
            {
                // Bypass privilege escalation entirely under the test harness.
                RunHelperInTestMode("write", target, backend);
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GpuModeController: using helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "write", modeStr, backend }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
                }
                else
                {
                    // Fallback: pkexec with inline content. Eco writes the full
                    // block artifact set; non-eco (only reachable in PCI mode
                    // here) writes just the trigger + backend marker and removes
                    // any stale modprobe/udev artifacts.
                    Logger.WriteLine($"GpuModeController: using pkexec fallback (mode={modeStr}, backend={backend})");
                    string script;
                    if (isEco)
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"cat > {ModprobeBlockPath} << 'GHELPER_BLOCK'\n{ModprobeBlockContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {ModprobeBlockPath}\n" +
                            $"cat > {UdevRemovePath} << 'GHELPER_BLOCK'\n{UdevRemoveContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    else
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"rm -f {ModprobeBlockPath} {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            if (File.Exists(TriggerPath))
            {
                Logger.WriteLine($"GpuModeController: trigger written successfully (mode={modeStr})");
                if (isEco)
                {
                    Logger.WriteLine($"  modprobe: {ModprobeBlockPath}");
                    Logger.WriteLine($"  udev:     {UdevRemovePath}");
                }
                Logger.WriteLine($"  trigger:  {TriggerPath} (content: {modeStr})");
                Logger.WriteLine($"  backend:  {BackendPath} (content: {backend})");
            }
            else
            {
                Logger.WriteLine("GpuModeController: driver block write failed (pkexec cancelled or error)");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: WriteDriverBlock failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove all driver block artifacts (current + legacy from previous approaches).
    /// Called when:
    /// - Eco is applied live (dgpu_disable=1 is persistent, block no longer needed)
    /// - Switching away from Eco (dGPU driver should be loadable)
    /// - Startup confirms hardware matches config
    /// - Boot safety check (MUX=0 + dgpu_disable=1 recovery)
    ///
    /// Prefers sudo helper (no tty needed). Falls back to pkexec.
    /// No-op if no artifacts exist (avoids unnecessary sudo/pkexec calls).
    /// </summary>
    private void RemoveDriverBlock()
    {
        try
        {
            if (!DriverBlockExists())
                return; // Nothing to remove

            Logger.WriteLine("GpuModeController: removing driver block artifacts (current + legacy)");

            if (IsTestMode)
            {
                RunHelperInTestMode("clean");
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Helper `clean` removes only the ephemeral Eco artifacts.
                // The backend marker is a persistent user preference and is
                // intentionally preserved here so the next boot still uses
                // the correct backend.
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "clean" }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
            }
            else
            {
                SysfsHelper.RunCommandWithTimeout("pkexec",
                    $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}", 120000);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: RemoveDriverBlock failed: {ex.Message}");
        }
    }

    // Config helpers

    private static void SaveModeToConfig(GpuMode mode)
    {
        string modeStr = mode switch
        {
            GpuMode.Eco => "eco",
            GpuMode.Standard => "standard",
            GpuMode.Optimized => "optimized",
            GpuMode.Ultimate => "ultimate",
            _ => "standard"
        };
        AppConfig.Set("gpu_mode", modeStr);
    }

    private static GpuMode ParseGpuMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "eco" => GpuMode.Eco,
            "standard" => GpuMode.Standard,
            "optimized" => GpuMode.Optimized,
            "ultimate" => GpuMode.Ultimate,
            _ => GpuMode.Standard
        };
    }
}
