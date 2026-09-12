# Windows installer

This application is packaged as an MSIX bundle. Building the installer requires Visual Studio with the **Universal Windows Platform development** workload and a Windows 10 SDK (10.0.22621.0 or later).

From the repository root, run:

```powershell
.\scripts\Build-WindowsInstaller.ps1
```

The build creates a signed x64 `.msixbundle`, its `.cer` certificate, and the package deployment files under `Artifacts`. For a different architecture, pass `-Platform x86` or `-Platform ARM64`.

Install the generated bundle on the target computer with:

```powershell
.\scripts\Install-WindowsInstaller.ps1 -BundlePath .\Artifacts\<package-folder>\Notepads_<version>_x64.msixbundle
```

The installer imports the accompanying development certificate into the current user's `TrustedPeople` store before calling `Add-AppxPackage`. Replace the generated development certificate with an organization-trusted code-signing certificate before distributing the application outside a controlled environment.
