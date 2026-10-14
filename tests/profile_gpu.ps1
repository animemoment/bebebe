$proc = Start-Process -FilePath "D:\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe" -ArgumentList "-s tests/run_gpu_load_test.gd" -PassThru -RedirectStandardOutput "test_output.log" -RedirectStandardError "test_err.log"
Start-Sleep -Seconds 3
$samples = @()
$nvidias = @()
for ($i = 0; $i -lt 5; $i++) {
    $nv = & 'C:\Windows\System32\nvidia-smi.exe' --query-gpu=utilization.gpu --format=csv,noheader,nounits
    if ($nv) { $nvidias += $nv.Trim() }
    
    $c = Get-Counter "\GPU Engine(*)\Utilization Percentage" -ErrorAction SilentlyContinue
    if ($c) {
        $godotSamples = $c.CounterSamples | Where-Object { $_.InstanceName -match "$($proc.Id)" -and $_.InstanceName -match "engtype_3D" }
        $sumVal = 0.0
        foreach ($s in $godotSamples) {
            $sumVal += $s.CookedValue
        }
        $samples += [math]::Round($sumVal, 2)
    }
    Start-Sleep -Seconds 1
}
$proc.WaitForExit(10000)
Write-Output "NVIDIA-SMI Total GPU Load: $($nvidias -join ', ') %"
Write-Output "Windows Counter 3D Engine Load for Godot PID $($proc.Id): $($samples -join ', ') %"
if (Test-Path "test_output.log") {
    Get-Content "test_output.log" | Select-Object -Last 12
}
