# 🔔 School Bell — ระบบกริ่งอัตโนมัติโรงเรียน

Version: `0.1.0-beta`

ระบบกริ่งอัตโนมัติสำหรับโรงเรียน พัฒนาด้วย ASP.NET Core 10 บน Linux (Raspberry Pi / VM)  
ตั้งเวลากริ่งและจัดการไฟล์เสียงผ่าน Web UI ได้เลย ไม่ต้อง SSH แก้ crontab อีกต่อไป

## Features

- ตั้งตารางเวลากริ่งรายวัน/รายสัปดาห์ผ่าน Web UI
- อัปโหลดไฟล์เสียง MP3 / WAV
- กดเล่น/หยุดเสียงทันที
- ระบบ Guest / Admin (ต้องใส่รหัสผ่านเพื่อแก้ไข)
- รองรับมือถือ (Mobile-friendly)

## Requirements

- Linux (Raspberry Pi OS / Debian / Ubuntu)
- .NET 10 SDK
- `mpg123` สำหรับไฟล์ MP3
- `alsa-utils` / `aplay` สำหรับไฟล์ WAV
- ตั้ง timezone ของเครื่อง server ให้ถูกต้อง เช่น `Asia/Bangkok`

---

## การติดตั้ง

### 1. ติดตั้ง .NET 10

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
source ~/.bashrc
dotnet --version
```

### 2. ติดตั้ง audio tools

```bash
sudo apt update
sudo apt install -y mpg123 alsa-utils
```

### 3. ตั้ง timezone เครื่อง server

```bash
sudo timedatectl set-timezone Asia/Bangkok
timedatectl
```

### 4. Clone โปรเจกต์

```bash
git clone https://github.com/tanapatwk/SchoolBell.git
cd SchoolBell/SchoolBell
```

### 5. ตั้งค่ารหัสผ่าน Admin

คัดลอกไฟล์ตัวอย่างแล้วแก้ไข

```bash
cp appsettings.example.json appsettings.json
nano appsettings.json
```

แก้ค่า `AdminPassword` เป็นรหัสผ่านที่ต้องการ

```json
{
  "AdminPassword": "your-password-here"
}
```

### 6. Run ทดสอบ

```bash
dotnet run --urls "http://0.0.0.0:5196"
```

เปิด browser ไปที่ `http://<IP>:5196`

---

## ตั้งให้รันอัตโนมัติด้วย systemd

### 1. Build แบบ Release
ให้แก้ `YOUR_USERNAME` ให้ตรงกับ username ของคุณ

```bash
cd ~/SchoolBell/SchoolBell
sudo mkdir -p /opt/schoolbell
sudo chown YOUR_USERNAME:YOUR_USERNAME /opt/schoolbell
dotnet publish -c Release -o /opt/schoolbell
```

### 2. สร้าง systemd service

```bash
sudo nano /etc/systemd/system/schoolbell.service
```

วางเนื้อหานี้ โดยแก้ `YOUR_USERNAME` ให้ตรงกับ username ของคุณ

```ini
[Unit]
Description=School Bell Automatic Bell System
After=network.target

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=/opt/schoolbell
ExecStart=/home/YOUR_USERNAME/.dotnet/dotnet /opt/schoolbell/SchoolBell.dll --urls "http://0.0.0.0:5196"
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

### 3. เปิดใช้งาน service

```bash
sudo systemctl daemon-reload
sudo systemctl enable schoolbell
sudo systemctl start schoolbell
```

### 4. ตรวจสอบสถานะ

```bash
sudo systemctl status schoolbell
```

### 5. ดู log

```bash
journalctl -u schoolbell -f
```

---

## คำสั่งที่ใช้บ่อย

| คำสั่ง | ความหมาย |
|---|---|
| `sudo systemctl start schoolbell` | เริ่ม service |
| `sudo systemctl stop schoolbell` | หยุด service |
| `sudo systemctl restart schoolbell` | restart service |
| `sudo systemctl status schoolbell` | ดูสถานะ |
| `journalctl -u schoolbell -f` | ดู log แบบ real-time |

---

## โครงสร้างโปรเจกต์

| ไฟล์/โฟลเดอร์ | หน้าที่ |
|---|---|
| `Data/AppDbContext.cs` | Entity Framework DbContext |
| `Jobs/BellJob.cs` | Quartz.NET Job ตรวจเวลากริ่ง |
| `Models/AudioFile.cs` | Model ไฟล์เสียง |
| `Models/Schedule.cs` | Model ตารางเวลา |
| `Services/AudioFileService.cs` | จัดการ upload/ลบไฟล์เสียง |
| `Services/AudioService.cs` | เล่น/หยุดเสียงผ่าน mpg123/aplay |
| `Services/ScheduleService.cs` | CRUD ตารางเวลา |
| `wwwroot/index.html` | Web UI |
| `appsettings.json` | Config รหัสผ่าน Admin |
| `Program.cs` | Entry point + API endpoints |

---

## Tech Stack

| ส่วน | เทคโนโลยี |
|---|---|
| Backend | ASP.NET Core 10 (Minimal API) |
| Scheduler | Quartz.NET |
| Database | SQLite + Entity Framework Core |
| Audio | mpg123 / aplay |
| Frontend | HTML + Vanilla JS |

---

## Known Bugs / Fixes

- Fixed: ป้องกันการกดเล่นเพลงรัว ๆ หรือ schedule เวลาซ้อนกันจนสร้าง `mpg123` / `aplay` หลาย process ค้างในระบบ โดยให้การหยุดและเริ่มเสียงเป็น atomic และให้ scheduler ข้ามรอบเมื่อมีเสียงกำลังเล่นอยู่
- Fixed: Production จะไม่ยอมเริ่มระบบถ้าไม่ได้ตั้ง `AdminPassword` หรือยังใช้ค่า default `admin1234`
- Fixed: เพิ่ม rate limit ให้ login และปุ่มเล่น/หยุดเสียง เพื่อลด brute force และการกดรัวเกินจำเป็น
- Fixed: เพิ่ม server-side validation สำหรับ upload, schedule, และการลบไฟล์เสียงที่ยังถูกใช้งานอยู่
- Fixed: ป้องกันการอัปโหลดไฟล์เสียงซ้ำด้วยชื่อไฟล์และขนาดไฟล์เดิม
- Fixed: ป้องกัน XSS จากชื่อไฟล์เสียงและชื่อตารางในหน้า Web UI
- Fixed: เพิ่ม session cookie hardening ด้วยชื่อ cookie เฉพาะระบบและ `SameSite=Strict`
- Added: เริ่มนับ version ที่ `0.1.0-beta` พร้อม dashboard/settings สำหรับชื่อระบบ โลโก้ และ reset ข้อมูลระบบ

---

## License

MIT
