# Arch packaging

This directory contains a first-party Arch Linux packaging prototype for
G-Helper Linux.

Before publishing to AUR or using it for a release:

1. Set `pkgver` to the released upstream version.
2. Generate real checksums with `updpkgsums`.
3. Build in a clean chroot if possible.

The package creates the `ghelper` group through `sysusers.d`, installs hardware
udev rules with group-only access, keeps `/opt/ghelper` root-owned, and limits
passwordless helper access to members of the `ghelper` group.

After installing:

```bash
sudo usermod -aG ghelper <user>
```

Reboot before launching G-Helper so group access, udev permissions, sysfs state,
and GPU boot helpers are applied from a clean session.
