# PS C:\Users\akzho> Invoke-ps2exe `
# -inputFile "C:\migrate\Projects\SteamTV\SteamTV_FINAL_FIXED_auto_src.ps1" `
# -outputFile "C:\migrate\Projects\SteamTV\SteamTVv4.exe" `
# -noConsole `
# -icon "C:\migrate\Projects\SteamTV\start_icon.ico"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Activity check
function Check-Status {
    return Test-Path "$env:TEMP\steam_tv_marker.txt"
}

function Get-BigPictureStatus {
    $bpProcess = Get-Process | Where-Object {
        $_.MainWindowTitle -like "*Big Picture*" -and $_.ProcessName -eq "steamwebhelper"
    }
    return [bool]$bpProcess
}

function Get-ControllerStatus {
    try {
        $controller = Get-PnpDevice | Where-Object {
            ($_.InstanceId -notmatch "BTHENUM") -and
            ($_.HardwareID -match "HID.*VID.*057E.*PID.*2009") -and
            ($_.HardwareID -match "UP:0001_U:0005") -and
            ($_.Status -eq "OK")
        }
        return [bool]$controller
    } catch {
        return $false
    }
}

function Start-Monitor {
    $tempFolder = "$env:TEMP\SteamTV_Controller_GUI"
    if (!(Test-Path $tempFolder)) {
        New-Item -ItemType Directory -Path $tempFolder | Out-Null
    }

    $mainScriptPath = Join-Path $tempFolder "SteamTV.ps1"
    $startBatPath = Join-Path $tempFolder "StartControllerMonitorAS.bat"
    $stopScriptPath = Join-Path $tempFolder "SteamTV_Manager_contr_BP stop.ps1"

    Set-Content -Path $mainScriptPath -Value @'
"[{0}] SteamTV Monitor started (PID: {1})" -f (Get-Date), $PID | Out-File "$env:TEMP\steam_tv_marker.txt"
[System.Diagnostics.Process]::GetCurrentProcess().Id | Out-File "$env:TEMP\steam_tv_pid.txt"
$host.UI.RawUI.WindowTitle = "SteamTV_Controller_Monitor_[PID:$PID]"

Import-Module DisplayConfig -Force

$steamPath = "C:\Program Files (x86)\Steam\steam.exe"
$targetDisplayNumber = 3

function Send-WOL {
    $adb = "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe"
    & $adb connect 192.168.1.102 | Out-Null
    Start-Sleep -Seconds 0

    $wakefulnessState = & $adb shell dumpsys power | Select-String "mWakefulness=Awake"
    $result = if ($wakefulnessState) { 1 } else { 0 }

    if ($result -eq 0) {
        Write-Host "📺 Screen is off. Turning on..."
        & $adb shell input keyevent 26
    } else {
        Write-Host "✅ Screen is already on."
    }
}

function Get-TargetDisplay {
    $allDisplays = Get-DisplayInfo
    $display = $allDisplays | Where-Object {
        [int]$_.DisplayId -eq $targetDisplayNumber
    }
    if (-not $display) { exit 1 }
    return $display
}

function Is-HDMI2-Active {
    $adb = "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe"
    $output = & $adb shell dumpsys activity activities | Select-String "ResumedActivity"

    return $output -match "com\.xiaomi\.mitv\.tvplayer\/\.ExternalSourceActivity"
}


function Get-ControllerStatus {
    try {
        $controller = Get-PnpDevice | Where-Object {
            $_.Class -eq "HIDClass" -and (
                (
                    ($_.HardwareID -match "VID_057E.*PID_2009" -or $_.InstanceId -match "VID&0002057E.*PID&2009") -and
                    $_.HardwareID -match "UP:0001_U:0005" -and
                    $_.Status -eq "OK"
                ) -or
                (
                    $_.HardwareID -match "VID_37D7.*PID_2501" -and
                    $_.HardwareID -match "UP:0001_U:0005" -and
                    $_.Present -eq $true
                )
            )
        }
        return [bool]$controller
    } catch {
        return $false
    }
}


function Get-BigPictureStatus {
    $bpProcess = Get-Process | Where-Object {
        $_.MainWindowTitle -like "*Big Picture*" -and $_.ProcessName -eq "steamwebhelper"
    }
    return [bool]$bpProcess
}

function TurnOffTV {
    & "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" disconnect 192.168.1.102
}

function Minimize-SteamWindow {
    Add-Type @"
    using System;
    using System.Runtime.InteropServices;
    public class Win32_Alt2 {
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
"@ -ErrorAction SilentlyContinue

    $windows = Get-Process steamwebhelper | Where-Object { $_.MainWindowTitle -eq "Steam" -and $_.MainWindowHandle -ne 0 }

    foreach ($win in $windows) {
        [Win32_Alt2]::ShowWindowAsync($win.MainWindowHandle, 6)  # SW_MINIMIZE

    }

    if (-not $windows) {
        Write-Host "Active Steam window not found in steamwebhelper"
    }
}


function SwitchTVSourceToPC {
    & "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" connect 192.168.1.102
    Start-Sleep -Seconds 1
    #& "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" shell am start -n com.xiaomi.mitv.tvplayer/.ExternalSourceActivity
    & "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" shell monkey -p com.liskovsoft.tvexternalsource.hdmi2 -c android.intent.category.LAUNCHER 1
}

function RestoreTVSourceBeforeShutdown {
    & "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" shell am start -n com.spocky.projengmenu/.ui.home.MainActivity
    & "C:\migrate\pc\steamTV\final3\platform-tools\adb.exe" disconnect 192.168.1.102
    Start-Sleep -Seconds 1
}





try {
    $display = Get-TargetDisplay
    $previousControllerStatus = $false
    $previousBPStatus = $false
    $displayEnabled = $false

    while ($true) {
        $currentControllerStatus = Get-ControllerStatus
        $currentBPStatus = Get-BigPictureStatus

        if ($currentControllerStatus -and !$previousControllerStatus -and !$currentBPStatus) {
            Send-WOL
            Enable-Display -DisplayId $display.DisplayId
            $displayEnabled = $true
            
            SwitchTVSourceToPC
            
            $timeout = 10
            $elapsed = 0.0
            while (-not (Is-HDMI2-Active) -and $elapsed -lt $timeout) {
                Start-Sleep -Milliseconds 200
                $elapsed += 0.2
            }

            if (Is-HDMI2-Active) {
                Start-Sleep -Seconds 1
                Start-Process $steamPath -ArgumentList "-start steam://open/bigpicture"
            }
        }

        if ($displayEnabled -and $previousBPStatus -and !$currentBPStatus) {
            Disable-Display -DisplayId $display.DisplayId
            RestoreTVSourceBeforeShutdown
            TurnOffTV
            Minimize-SteamWindow
            $displayEnabled = $false
            Minimize-SteamWindow

        }

        $previousControllerStatus = $currentControllerStatus
        $previousBPStatus = $currentBPStatus
        Start-Sleep -Seconds 2
    }
} catch {
    exit 1
}





'@ -Encoding UTF8

Set-Content -Path $startBatPath -Value @'
@echo off
if "%1"=="hidden" goto :main

start "" /min "%~f0" hidden
exit /b

:main
set "REG_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "ENTRY_NAME=SteamTV_Controller"
set "BAT_PATH=%~f0 hidden"

reg add "%REG_KEY%" /v "%ENTRY_NAME%" /t REG_SZ /d "%BAT_PATH%" /f >nul

start "" /min pwsh -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0SteamTV.ps1"
exit
'@ -Encoding UTF8



    Set-Content -Path $stopScriptPath -Value @'
Get-CimInstance Win32_Process -Filter "name = 'pwsh.exe'" |
    Where-Object {
        $_.CommandLine -match "SteamTV.ps1" -and $_.CommandLine -match "-File"
    } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force
    }

Remove-Item "$env:TEMP\steam_tv_marker.txt" -ErrorAction SilentlyContinue
'@ -Encoding UTF8

    Start-Process -FilePath $startBatPath
}


function Stop-Monitor {
    try {
        $pidPath = "$env:TEMP\steam_tv_pid.txt"
        if (Test-Path $pidPath) {
            $pipid = Get-Content $pidPath | Select-Object -First 1
            $pipid = $pipid.Trim()

            if ($pipid -match '^\d+$') {
                Stop-Process -Id ([int]$pipid) -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Failed to stop process: $_", "SteamTV.exe", 'OK', 'Error')
    }

    $regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Remove-ItemProperty -Path $regPath -Name "SteamTV_Controller" -ErrorAction SilentlyContinue
    Remove-Item -Path "$env:TEMP\SteamTV_Controller_GUI" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\steam_tv_marker.txt" -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\steam_tv_pid.txt" -ErrorAction SilentlyContinue
}


# Control GUI
$form = New-Object Windows.Forms.Form
$form.Text = "SteamTV Monitor"
$form.Size = New-Object Drawing.Size(300,180)
$form.StartPosition = "CenterScreen"
$form.Topmost = $true

$statusLabel = New-Object Windows.Forms.Label
$statusLabel.Location = New-Object Drawing.Point(20,20)
$statusLabel.Size = New-Object Drawing.Size(240,20)
$form.Controls.Add($statusLabel)

$startButton = New-Object Windows.Forms.Button
$startButton.Text = "Start"
$startButton.Location = New-Object Drawing.Point(20,60)
$startButton.Size = New-Object Drawing.Size(80,30)
$startButton.Add_Click({ Start-Monitor })
$form.Controls.Add($startButton)

$stopButton = New-Object Windows.Forms.Button
$stopButton.Text = "Stop"
$stopButton.Location = New-Object Drawing.Point(110,60)
$stopButton.Size = New-Object Drawing.Size(80,30)
$stopButton.Add_Click({ Stop-Monitor })
$form.Controls.Add($stopButton)

$exitButton = New-Object Windows.Forms.Button
$exitButton.Text = "Exit"
$exitButton.Location = New-Object Drawing.Point(200,60)
$exitButton.Size = New-Object Drawing.Size(60,30)
$exitButton.Add_Click({ 
    $timer.Stop()
    $form.Close()
})
$form.Controls.Add($exitButton)

if (Check-Status) {
    $statusLabel.Text = "Status: Running"
    $statusLabel.ForeColor = "Green"
} else {
    $statusLabel.Text = "Status: Stopped"
    $statusLabel.ForeColor = "Red"
}

$timer = New-Object Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick([System.EventHandler]{
    param($sender, $eventArgs)
    if (Check-Status) {
        $statusLabel.Text = "Status: Running"
        $statusLabel.ForeColor = "Green"
    } else {
        $statusLabel.Text = "Status: Stopped"
        $statusLabel.ForeColor = "Red"
    }
}) | Out-Null
$timer.Start()

[void]$form.ShowDialog()
