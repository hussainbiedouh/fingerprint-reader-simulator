# Virtual Fingerprint Simulator - Setup

Manual setup for the test-signed virtual fingerprint driver on Windows 11 Pro, build 26200.

## Warnings

- **Secure Boot** can block test signing. If `bcdedit /set testsigning on` reports "protected by Secure Boot policy", reboot, enter BIOS/UEFI, disable Secure Boot, and suspend BitLocker if enabled.
- **Reboot is required** after enabling TestSigning and after changing Secure Boot. No script reboots automatically.
- Run every setup script from an elevated shell: right-click PowerShell and select **Run as administrator**. Bypass a restrictive policy with `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1`.
- When testing is done, run `bcdedit /set testsigning off`, reboot, and re-enable Secure Boot / BitLocker.

## Ordered steps

1. **Inspect the machine (no admin needed):**

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Environment.ps1
   ```

   Expect build 26200, WDK present, `devgen.exe` found, WbioSrvc `Stopped` / `Disabled`, TestSigning `Off`.

2. **Enable TestSigning (admin):**

   ```powershell
   .\scripts\Setup-TestSigning.ps1
   ```

   Reads `bcdedit /enum {current}`, runs `bcdedit /set testsigning on` when the state is off, reports Secure Boot, and prints a reboot reminder. Reboot manually; confirm the **Test Mode** watermark.

3. **Start WbioSrvc (admin):**

   ```powershell
   .\scripts\Enable-WbioSrvc.ps1
   ```

   Sets `StartupType` to `Manual`, starts the service, and verifies `Running` / `Manual`.

4. **Install the driver and devices (admin):**

   ```powershell
   .\scripts\Install-VirtualFingerprint.ps1
   ```

   Installs `package\VirtualFingerprint.cer` into LocalMachine `Root` and `TrustedPublisher` when present. Creates two persistent root devices with `devgen /add /bus ROOT /hardwareid <ID>`, then runs `pnputil /add-driver package\VirtualFingerprint.inf /install`.

   `pnputil /add-driver` accepts `.inf` only, not `.inx`. If the build produces just `VirtualFingerprint.inx`, the script copies it to a sibling `VirtualFingerprint.inf` before installing; keep any referenced driver files in `package\`. A `.inf` newer than the `.inx` is treated as the build output and is never overwritten. The install script rejects unresolved `$TOKEN$` placeholders before staging, so the `.inx` must be install-ready. Expand tokens with WDK `stampinf` (its `-u` option sets `$UMDFVERSION$`) or ship a generated `.inf` instead.

5. **Verify:**

   ```powershell
   .\scripts\Verify-Environment.ps1
   pnputil /enum-devices /deviceid "ROOT\WFP_SENSOR"
   pnputil /enum-devices /deviceid "ROOT\WFP_CONTROL"
   pnputil /enum-devices /problem
   ```

   Both devices should appear without a problem code in Device Manager; `Get-PnpDevice` works too. Installation errors are logged in `%windir%\inf\setupapi.dev.log`.

6. **Uninstall (admin):**

   ```powershell
   .\scripts\Uninstall-VirtualFingerprint.ps1
   ```

   Removes both root devices with `pnputil /remove-device`, finds the driver package via `Get-WindowsDriver -Online` (locale-independent), and deletes it with `pnputil /delete-driver oemN.inf /uninstall /force`. Safe to run repeatedly. The test certificate and TestSigning boot setting are left in place; remove them manually if needed (see Warnings).

## Hardware IDs

| Device | Hardware ID |
| --- | --- |
| Sensor | `ROOT\WFP_SENSOR` |
| Control | `ROOT\WFP_CONTROL` |

## References

- PnPUtil command syntax: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- PnPUtil examples: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples
- DevGen command syntax: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devgen-command-syntax
- DevGen examples: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devgen-examples
- DevCon install and its DevGen replacement: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devcon-install
- TestSigning boot option and Secure Boot: https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- Manual driver package deployment: https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/test-a-driver-package-manual-deployment
- Hardware IDs for root-enumerated devices: https://learn.microsoft.com/en-us/windows-hardware/drivers/install/hardware-ids
- Get-WindowsDriver (driver-store enumeration): https://learn.microsoft.com/en-us/powershell/module/dism/get-windowsdriver
