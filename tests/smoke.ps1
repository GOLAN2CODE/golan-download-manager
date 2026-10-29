param(
    [string]$GdmExe = "",
    [int]$Port = 18851
)

$ErrorActionPreference = "Stop"

# Ensure console output encoding is UTF-8 so redirected CLI output preserves Arabic text
$prevOutputEncoding = [Console]::OutputEncoding
$null = & cmd.exe /c "chcp 65001 >nul"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($GdmExe)) {
    $GdmExe = Join-Path $repoRoot "src\GDM\bin\Release\net10.0-windows\GDM.exe"
}
$GdmExe = [System.IO.Path]::GetFullPath($GdmExe)

if (-not (Test-Path -LiteralPath $GdmExe)) {
    Write-Host "FAIL: GDM.exe not found at $GdmExe" -ForegroundColor Red
    exit 1
}

$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$tempBinDir = Join-Path ([System.IO.Path]::GetTempPath()) "gdm-smoke-bin-$runId"
$tempHomeBase = Join-Path ([System.IO.Path]::GetTempPath()) "gdm-smoke-home-$runId"
$startedPids = [System.Collections.Generic.List[int]]::new()
$serverProc = $null
$prevGdmHome = $env:GDM_HOME
$exitCode = 0

function Stop-OwnedProcess([int]$PidToKill) {
    if ($PidToKill -le 0) { return }
    try {
        $p = Get-Process -Id $PidToKill -ErrorAction SilentlyContinue
        if ($p -and -not $p.HasExited) {
            Stop-Process -Id $PidToKill -Force -ErrorAction SilentlyContinue
            $p.WaitForExit(5000) | Out-Null
        }
    } catch { }
}

function Assert-Condition([bool]$Condition, [string]$Name, [string]$Detail = "") {
    if ($Condition) {
        Write-Host ("  [PASS] {0}{1}" -f $Name, $(if ($Detail) { " ($Detail)" } else { "" }))
    } else {
        Write-Host ("  [FAIL] {0}{1}" -f $Name, $(if ($Detail) { " ($Detail)" } else { "" })) -ForegroundColor Red
        throw "Smoke check failed: $Name $Detail"
    }
}

function Invoke-GdmCli([string]$ExePath, [string]$HomeDir, [string[]]$CliArgs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExePath
    $quotedArgs = foreach ($a in $CliArgs) {
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    $psi.Arguments = ($quotedArgs -join " ")
    $psi.WorkingDirectory = Split-Path -Parent $ExePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["GDM_HOME"] = $HomeDir

    $proc = [System.Diagnostics.Process]::Start($psi)
    $startedPids.Add($proc.Id) | Out-Null
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit(15000)) {
        Stop-OwnedProcess $proc.Id
        throw "CLI command timed out: $($CliArgs -join ' ')"
    }
    $proc.WaitForExit()
    return [PSCustomObject]@{
        ExitCode = $proc.ExitCode
        StdOut   = $stdoutTask.GetAwaiter().GetResult().Trim()
        StdErr   = $stderrTask.GetAwaiter().GetResult().Trim()
    }
}

try {
    Write-Host "=== Golan (GDM) Post-Publish Smoke Test ==="
    New-Item -ItemType Directory -Force -Path $tempBinDir, $tempHomeBase | Out-Null

    # 1. Copy GDM.exe and runtime assemblies (excluding any i18n directory) to %TEMP% far from source tree
    $srcBinDir = Split-Path -Parent $GdmExe
    $exeLeaf = Split-Path -Leaf $GdmExe
    Get-ChildItem -LiteralPath $srcBinDir -File | Where-Object {
        $_.Extension -in @(".exe", ".dll", ".json")
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $tempBinDir $_.Name) -Force
    }

    $isolatedExe = Join-Path $tempBinDir $exeLeaf
    Assert-Condition (Test-Path -LiteralPath $isolatedExe) "Copied $exeLeaf to isolated temp directory" $tempBinDir

    # Verify no i18n folder exists anywhere in tempBinDir or up to 6 parent directories
    $probeDir = $tempBinDir
    for ($i = 0; $i -lt 6 -and $probeDir; $i++) {
        $hasI18n = (Test-Path (Join-Path $probeDir "i18n")) -or (Test-Path (Join-Path $probeDir "src\GDM\i18n"))
        Assert-Condition (-not $hasI18n) "No disk i18n folder at ancestor level $i" $probeDir
        $probeDir = Split-Path -Parent $probeDir
    }

    # 2. Ensure local testserver.py is running on $Port with served/smoke.bin
    $engDir = Join-Path $scriptDir "engine"
    $servedDir = Join-Path $engDir "served"
    New-Item -ItemType Directory -Force -Path $servedDir | Out-Null
    $smokeFile = Join-Path $servedDir "smoke.bin"
    $smokeBytes = [System.Text.Encoding]::UTF8.GetBytes("GdmV2-Smoke-Test-Payload-" + ("X" * 4096))
    [System.IO.File]::WriteAllBytes($smokeFile, $smokeBytes)

    $portListening = $false
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $connectTask = $tcp.ConnectAsync("127.0.0.1", $Port)
        if ($connectTask.Wait(300) -and $tcp.Connected) { $portListening = $true }
        $tcp.Dispose()
    } catch { }

    if (-not $portListening) {
        $testServerPy = Join-Path $engDir "testserver.py"
        $serverProc = Start-Process -FilePath "python" -ArgumentList @($testServerPy, $servedDir, "$Port") -WorkingDirectory $engDir -WindowStyle Hidden -PassThru
        $startedPids.Add($serverProc.Id) | Out-Null
        $ready = $false
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Milliseconds 100
            try {
                $tcp = [System.Net.Sockets.TcpClient]::new()
                if ($tcp.ConnectAsync("127.0.0.1", $Port).Wait(200) -and $tcp.Connected) {
                    $tcp.Dispose()
                    $ready = $true
                    break
                }
                $tcp.Dispose()
            } catch { }
        }
        Assert-Condition $ready "Local testserver.py listening on port $Port" "PID=$($serverProc.Id)"
    }

    # 3. Run smoke verification for both Arabic ('ar') and English ('en')
    foreach ($lang in @("ar", "en")) {
        Write-Host "--- Testing language: $lang ---"
        $langHome = Join-Path $tempHomeBase $lang
        $roamingDir = Join-Path $langHome "Roaming"
        $localDir = Join-Path $langHome "Local"
        $dlDir = Join-Path $langHome "Downloads"
        $tempDir = Join-Path $langHome "Temp"
        New-Item -ItemType Directory -Force -Path $roamingDir, $localDir, $dlDir, $tempDir | Out-Null

        $settingsObj = @{
            FirstRun                 = $false
            Language                 = $lang
            DownloadRoot             = $dlDir
            DownloadFolder           = $dlDir
            TempDir                  = $tempDir
            UseCategoryFolders       = $false
            ShowAddDialogFromBrowser = $false
            ShowStartDialog          = $false
            ShowProgressWindow       = $false
            ShowCompleteDialog       = $false
            ClipboardMonitor         = $false
            SoundsEnabled            = $false
            EnableSounds             = $false
            CloseToTray              = $false
        }
        $settingsPath = Join-Path $roamingDir "settings.json"
        [System.IO.File]::WriteAllText($settingsPath, ($settingsObj | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)

        $env:GDM_HOME = $langHome

        # Start GDM.exe --background and capture its PID
        $bgPsi = [System.Diagnostics.ProcessStartInfo]::new()
        $bgPsi.FileName = $isolatedExe
        $bgPsi.Arguments = "--background"
        $bgPsi.WorkingDirectory = $tempBinDir
        $bgPsi.UseShellExecute = $false
        $bgPsi.CreateNoWindow = $true
        $bgPsi.EnvironmentVariables["GDM_HOME"] = $langHome

        $bgProc = [System.Diagnostics.Process]::Start($bgPsi)
        $startedPids.Add($bgProc.Id) | Out-Null
        Assert-Condition ($null -ne $bgProc -and -not $bgProc.HasExited) "[$lang] Started GDM.exe --background" "PID=$($bgProc.Id)"

        try {
            # Wait until background IPC pipe is ready by polling list --json
            $pipeReady = $false
            for ($attempt = 0; $attempt -lt 50; $attempt++) {
                Start-Sleep -Milliseconds 200
                $probe = Invoke-GdmCli -ExePath $isolatedExe -HomeDir $langHome -CliArgs @("list", "--json")
                if ($probe.ExitCode -eq 0 -and $probe.StdOut.StartsWith("[")) {
                    $pipeReady = $true
                    break
                }
            }
            Assert-Condition $pipeReady "[$lang] Background instance IPC pipe ready"

            $arUsage = [regex]::Unescape("\u0627\u0644\u0627\u0633\u062a\u062e\u062f\u0627\u0645:")
            $arAdded = [regex]::Unescape("\u062a\u0645\u062a \u0625\u0636\u0627\u0641\u0629 \u0627\u0644\u062a\u0646\u0632\u064a\u0644")

            # Test --help
            $helpRes = Invoke-GdmCli -ExePath $isolatedExe -HomeDir $langHome -CliArgs @("--help")
            Assert-Condition ($helpRes.ExitCode -eq 0) "[$lang] GDM.exe --help exit code 0"
            Assert-Condition (-not ($helpRes.StdOut -match "^cli\.")) "[$lang] --help output is not a raw dotted key"
            if ($lang -eq "ar") {
                Assert-Condition ($helpRes.StdOut.Contains($arUsage)) "[ar] --help contains localized Arabic text"
            } else {
                Assert-Condition ($helpRes.StdOut.Contains("Usage:")) "[en] --help contains localized English text 'Usage:'"
            }

            # Test add <local testserver url>
            $testUrl = "http://127.0.0.1:$Port/smoke.bin?lang=$lang"
            $targetName = "smoke-$lang.bin"
            $addRes = Invoke-GdmCli -ExePath $isolatedExe -HomeDir $langHome -CliArgs @("add", $testUrl, "--name", $targetName, "--folder", $dlDir, "--start")
            Assert-Condition ($addRes.ExitCode -eq 0) "[$lang] GDM.exe add exit code 0" $addRes.StdOut
            Assert-Condition (-not ($addRes.StdOut -match "^cli\.")) "[$lang] add output is not a raw dotted key"
            if ($lang -eq "ar") {
                Assert-Condition ($addRes.StdOut.Contains($arAdded)) "[ar] add output contains localized Arabic confirmation" $addRes.StdOut
            } else {
                Assert-Condition ($addRes.StdOut.Contains("Download added")) "[en] add output contains localized English confirmation" $addRes.StdOut
            }

            # Test list --json and wait for download completion
            $completedItem = $null
            for ($attempt = 0; $attempt -lt 50; $attempt++) {
                $listRes = Invoke-GdmCli -ExePath $isolatedExe -HomeDir $langHome -CliArgs @("list", "--json")
                Assert-Condition ($listRes.ExitCode -eq 0) "[$lang] list --json exit code 0"
                $items = $listRes.StdOut | ConvertFrom-Json
                if ($items -isnot [System.Array]) { $items = @($items) }
                $match = $items | Where-Object { $_.FileName -eq $targetName -or $_.Url -eq $testUrl } | Select-Object -First 1
                if ($match -and $match.Status -eq "Completed") {
                    $completedItem = $match
                    break
                }
                Start-Sleep -Milliseconds 200
            }
            Assert-Condition ($null -ne $completedItem) "[$lang] Download listed in list --json and reached Completed"
            $downloadedPath = Join-Path $dlDir $targetName
            Assert-Condition (Test-Path -LiteralPath $downloadedPath) "[$lang] Downloaded file exists on disk" $downloadedPath

            # Check <GDM_HOME>\Local\gdm.log has no exceptions
            $logPath = Join-Path $localDir "gdm.log"
            if (Test-Path -LiteralPath $logPath) {
                $logContent = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
                $hasError = $logContent -match "\[ERR\]|Exception|(?i)\b(ui error|failed:)"
                if ($hasError) { Write-Host "---- gdm.log ----"; Write-Host $logContent; Write-Host "-----------------" }
                Assert-Condition (-not $hasError) "[$lang] gdm.log has no exceptions"
            } else {
                Assert-Condition $true "[$lang] gdm.log has no exceptions (no errors logged)"
            }
        } finally {
            Stop-OwnedProcess $bgProc.Id
        }
    }

    Write-Host "ALL SMOKE CHECKS PASSED" -ForegroundColor Green
    $exitCode = 0
} catch {
    Write-Host "SMOKE TEST FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    $env:GDM_HOME = $prevGdmHome
    [Console]::OutputEncoding = $prevOutputEncoding

    foreach ($pidToStop in $startedPids) {
        Stop-OwnedProcess $pidToStop
    }

    # Retry deleting temp directories briefly in case child process handles are releasing
    for ($i = 0; $i -lt 10; $i++) {
        try {
            if (Test-Path -LiteralPath $tempBinDir) { Remove-Item -LiteralPath $tempBinDir -Recurse -Force -ErrorAction Stop }
            if (Test-Path -LiteralPath $tempHomeBase) { Remove-Item -LiteralPath $tempHomeBase -Recurse -Force -ErrorAction Stop }
            break
        } catch {
            Start-Sleep -Milliseconds 200
        }
    }
}

exit $exitCode
