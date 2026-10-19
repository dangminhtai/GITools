$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$fixture = Join-Path $PSScriptRoot ('fps-' + [guid]::NewGuid().ToString('N') + '.json')
Copy-Item -LiteralPath 'C:\GCT\Backends\z3lx-7.1.0\plugin_config.json' -Destination $fixture
$before = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
$appPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'App\GCT.Fov.exe'
$p = Start-Process -FilePath $appPath -ArgumentList @('--config', $fixture) -WindowStyle Hidden -PassThru
$results = [Collections.Generic.List[string]]::new()
try {
    for ($i = 0; $i -lt 50; $i++) { Start-Sleep -Milliseconds 100; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
    Start-Sleep -Milliseconds 700
    $w = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    function Find-Control($id) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $element = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $element) { throw "Missing $id" }
        return $element
    }
    $input = Find-Control 'FpsInput'
    $cameraToggle = (Find-Control 'KeyboardStart').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($cameraToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw 'Camera checkbox is not checked by default' }
    $requestPath = Join-Path $PSScriptRoot 'pose_arg_trial_request.txt'
    if (Test-Path -LiteralPath $requestPath) { throw 'Unexpected pre-existing camera request in isolated verification folder' }
    $cameraToggle.Toggle(); Start-Sleep -Milliseconds 400
    if ($cameraToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Camera checkbox cannot be unchecked offline' }
    $cameraToggle.Toggle(); Start-Sleep -Milliseconds 400
    if ($cameraToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw 'Camera checkbox cannot queue activation offline' }
    if (Test-Path -LiteralPath $requestPath) { throw 'Offline camera checkbox published a request without a backend' }
    $results.Add('PASS: camera checkbox defaults checked; offline uncheck/recheck changes intent without publishing a camera request')
    $button = Find-Control 'ApplyFpsButton'
    $set = $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $click = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    # Keep the same sharing mode used by the plugin while testing in-place writes.
    $handle = [IO.File]::Open($fixture, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $set.SetValue('144'); $click.Invoke(); Start-Sleep -Milliseconds 700
        $after = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
        if ($after.targetFps -ne 144 -or -not $after.unlockFps) { throw 'FPS write failed' }
        foreach ($field in $before.PSObject.Properties.Name) {
            if ($field -in @('targetFps','unlockFps')) { continue }
            if (($before.$field | ConvertTo-Json -Compress) -ne ($after.$field | ConvertTo-Json -Compress)) { throw "FPS changed unrelated field: $field" }
        }
        $results.Add('PASS: FPS button writes targetFps/unlockFps in place and preserves all FOV/other fields')
        $hash = (Get-FileHash -LiteralPath $fixture).Hash
        foreach ($invalid in @('0','-1','1001','abc')) {
            $set.SetValue($invalid); $click.Invoke(); Start-Sleep -Milliseconds 200
            if ((Get-FileHash -LiteralPath $fixture).Hash -ne $hash) { throw "Invalid input wrote config: $invalid" }
        }
        $results.Add('PASS: invalid FPS values do not write config')
        $slider = Find-Control 'FovSlider'
        $slider.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(75)
        Start-Sleep -Milliseconds 700
        $afterFov = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
        if ($afterFov.targetFov -ne 75 -or $afterFov.targetFps -ne 144) { throw 'FOV regression' }
        $results.Add('PASS: original FOV auto-send still works and preserves requested FPS')
        $set.SetValue('120'); $click.Invoke()
        $slider.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(90)
        Start-Sleep -Milliseconds 900
        $both = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
        if ($both.targetFov -ne 90 -or $both.targetFps -ne 120) { throw 'Concurrent FOV/FPS lost update' }
        $results.Add('PASS: overlapping FPS/FOV requests preserve both values')
    }
    finally { $handle.Dispose() }
    $results.Add('NOT TESTED: actual game FPS')
}
catch { $results.Add('FAIL: ' + $_.Exception.Message); throw }
finally {
    $results | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'fps-results.txt') -Encoding UTF8
    $p.Refresh(); if (-not $p.HasExited) { $null = $p.CloseMainWindow(); $null = $p.WaitForExit(10000) }
}
$results
