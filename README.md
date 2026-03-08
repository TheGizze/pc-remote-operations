# remote_operations

A local network API for remotely rebooting a machine and selecting which OS to boot into (useful for dual/multi-boot setups).

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Windows:** must run as Administrator (required for `bcdedit` and `shutdown`)
- **Linux:** must run as root or with sudo (required for `grub-reboot` and `shutdown`)

## Running

```bash
dotnet run
```

The API listens on **port 5000** on all network interfaces, so it is reachable from any device on your local network:

```
http://<machine-ip>:5000
```

To find your machine's local IP on Windows: `ipconfig` → look for IPv4 Address under your active adapter.

---

## API Reference

### List boot entries

Returns all bootable OS entries on the machine. Use the `id` values here when calling the reboot endpoint.

```
GET /reboot/entries
```

**Example response (Windows):**
```json
[
  { "id": "{current}", "description": "Windows 11", "isDefault": true },
  { "id": "{abc12345-...}", "description": "Ubuntu", "isDefault": false }
]
```

**Example response (Linux):**
```json
[
  { "id": "0", "description": "Ubuntu 24.04 LTS", "isDefault": true },
  { "id": "1", "description": "Windows Boot Manager", "isDefault": false }
]
```

---

### Reboot

Reboots the machine. Optionally set `targetEntryId` to boot into a specific OS on the next restart. Omit it (or leave it `null`) to reboot into the current default OS.

```
POST /reboot
Content-Type: application/json
```

**Reboot into default OS:**
```json
{}
```

**Reboot into a specific OS:**
```json
{
  "targetEntryId": "{abc12345-0000-0000-0000-000000000000}"
}
```

**Example response:**
```json
{
  "message": "Reboot initiated → {abc12345-0000-0000-0000-000000000000}",
  "targetEntryId": "{abc12345-0000-0000-0000-000000000000}"
}
```

> The machine will reboot after a 5-second delay, giving the API time to send the response.

---

## Typical workflow

1. **Find the OS entry ID** — call `GET /reboot/entries` from another device on the network
2. **Trigger the reboot** — call `POST /reboot` with the desired `targetEntryId`

```bash
# Step 1 — list entries
curl http://192.168.1.100:5000/reboot/entries

# Step 2 — reboot into a specific entry
curl -X POST http://192.168.1.100:5000/reboot \
  -H "Content-Type: application/json" \
  -d '{ "targetEntryId": "{abc12345-0000-0000-0000-000000000000}" }'
```

---

## Installing as a service

Running as a service means the API starts automatically at boot without anyone needing to log in. The steps below publish a self-contained executable (no .NET SDK required on the machine at runtime) and register it with the OS service manager.

---

### Windows (Task Scheduler / sc.exe)

#### 1. Publish a self-contained executable

Run this from the project directory:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o C:\Services\remote_operations
```

#### 2. Register the service

Open **PowerShell as Administrator** and run:

```powershell
sc.exe create RemoteOperations `
  binPath= "C:\Services\remote_operations\remote_operations.exe" `
  DisplayName= "Remote Operations API" `
  start= auto
```

> Note: the space after `binPath=`, `DisplayName=`, and `start=` is required by `sc.exe`.

#### 3. Configure the service to run as Local System

This grants the Administrator-level rights needed for `bcdedit` and `shutdown`:

```powershell
sc.exe config RemoteOperations obj= LocalSystem
```

#### 4. Start the service

```powershell
sc.exe start RemoteOperations
```

#### Useful management commands

```powershell
sc.exe stop    RemoteOperations   # stop the service
sc.exe start   RemoteOperations   # start the service
sc.exe delete  RemoteOperations   # uninstall the service
sc.exe query   RemoteOperations   # check current status

# View logs in Event Viewer:
eventvwr.msc  # navigate to Windows Logs → Application, filter by source "remote_operations"
```

---

### Linux (systemd)

#### 1. Publish a self-contained executable

```bash
dotnet publish -c Release -r linux-x64 --self-contained -o /opt/remote_operations
chmod +x /opt/remote_operations/remote_operations
```

#### 2. Create a systemd unit file

Create the file `/etc/systemd/system/remote-operations.service`:

```bash
sudo nano /etc/systemd/system/remote-operations.service
```

Paste the following content:

```ini
[Unit]
Description=Remote Operations API
After=network.target

[Service]
Type=simple
ExecStart=/opt/remote_operations/remote_operations
WorkingDirectory=/opt/remote_operations
Restart=on-failure
RestartSec=10

# Run as root so the service can call grub-reboot and shutdown
User=root

# Log to the systemd journal (view with: journalctl -u remote-operations)
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

#### 3. Enable and start the service

```bash
sudo systemctl daemon-reload
sudo systemctl enable remote-operations   # start automatically on boot
sudo systemctl start remote-operations    # start now
```

#### 4. Verify it is running

```bash
sudo systemctl status remote-operations
```

#### Useful management commands

```bash
sudo systemctl stop    remote-operations   # stop the service
sudo systemctl start   remote-operations   # start the service
sudo systemctl restart remote-operations   # restart the service
sudo systemctl disable remote-operations   # remove from startup

# View live logs:
journalctl -u remote-operations -f
```

---

## Platform notes

| | Windows | Linux |
|---|---|---|
| Boot entry source | `bcdedit /enum all /v` | `/boot/grub/grub.cfg` |
| Set next boot entry | `bcdedit /bootsequence {id}` | `grub-reboot <index>` |
| Reboot command | `shutdown /r /t 5` | `shutdown -r now` |
| Required privilege | Administrator | root / sudo |

## OpenAPI / Swagger

The OpenAPI schema is available at:
```
http://<machine-ip>:5000/openapi/v1.json
```
