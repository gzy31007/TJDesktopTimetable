# Assertions over exported, mounted WinUI observations. No alternative layout model.
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function AssertSession($observations, $plan) {
    Require ($observations.Count -eq $plan.Count) 'Missing session observations.'
    for ($i=0; $i -lt $plan.Count; $i++) {
        Require ($observations[$i].Index -eq $i -and $observations[$i].Action -eq $plan[$i].Action) "Wrong session observation at $i."
    }
}
function AssertBoard($observations) {
    for ($i=0; $i -lt $observations.Count; $i++) {
        $o = $observations[$i]; $b = $o.Board; $scroll = $b.Scroll
        Require ($scroll.Geometry.IsLoaded -and $b.Canvas.IsLoaded -and $b.Blocks.Count -gt 0) "Board not mounted at $i."
        Require ($b.WeekdayHeader.Geometry.IsLoaded) "Fixed weekday header missing at $i."
        foreach ($block in $b.Blocks) {
            $height = ($block.Rows | ForEach-Object { $_.Observation.Geometry.ActualHeight } | Measure-Object -Sum).Sum
            Require ($block.Rows.Count -eq 3 -and $height -le ($block.Geometry.ActualHeight - $block.Padding.Top - $block.Padding.Bottom + 0.2)) "Clipped three-line block at $i."
            Require ($block.Rows[1].Observation.Geometry.ActualHeight -gt 0) "Room line collapsed at $i."
        }
        if ($o.Action -eq 'board-resize' -and $null -ne $o.Requested.ThresholdDelta) {
            if ($o.Requested.ThresholdDelta -lt 0) { Require ($scroll.ScrollableHeight -gt 0) 'Low threshold must scroll.' }
            else { Require ($scroll.ScrollableHeight -eq 0) 'Sufficient height still scrolls.' }
        }
        if ($o.Action -eq 'board-scroll' -and $o.Requested.VerticalOffset -eq 10000) {
            Require ([Math]::Abs($scroll.VerticalOffset - $scroll.ScrollableHeight) -lt 0.1) 'Last row not reachable.'
        }
        if ($i -gt 0 -and $o.Action -eq 'board-scroll') {
            $previous = $observations[$i-1].Board
            Require ([Math]::Abs($b.WeekdayHeader.Geometry.Y - $previous.WeekdayHeader.Geometry.Y) -lt 0.1) 'Weekday header moved vertically.'
            $dx = $scroll.HorizontalOffset - $previous.Scroll.HorizontalOffset
            Require ([Math]::Abs(($b.WeekdayHeader.Strip.X - $previous.WeekdayHeader.Strip.X) + $dx) -lt 0.1) 'Weekday header not following horizontal scroll.'
        }
    }
    # Same content retains scroll; view/range changes reset it.
    foreach ($i in @(4,5,6,7)) { Require ([Math]::Abs($observations[$i].Board.Scroll.VerticalOffset - 80) -lt 0.1) "Offset lost at $i." }
    foreach ($i in @(10,11,16,17)) { Require ($observations[$i].Board.Scroll.VerticalOffset -eq 0) "Range change did not reset at $i." }
}
function AssertUpdate($observations) {
    $check = ConvertFrom-Json '"\u68c0\u67e5\u66f4\u65b0"'
    $download = ConvertFrom-Json '"\u4e0b\u8f7d"'
    $skip = ConvertFrom-Json '"\u8df3\u8fc7\u6b64\u7248\u672c"'
    $states = @($null,'UpdateAvailable','UpdateAvailable','Skipped','Skipped','UpdateAvailable','UpdateAvailable','UpToDate','UpToDate','UpdateAvailable','UpdateAvailable')
    Require ($observations.Count -eq $states.Count) 'Wrong update observation count.'
    for ($i=0; $i -lt $states.Count; $i++) {
        $o = $observations[$i]
        $status = if ($null -eq $o.Update) { $null } else { $o.Update.Status }
        Require ($status -eq $states[$i]) "Incorrect update state at $i."
        $present = if ($states[$i] -eq 'UpdateAvailable') { 1 } else { 0 }
        Require ($o.DownloadButton.Count -eq $present) "Stale/missing widget button at $i."
        Require ($o.SettingsButtons -contains $check) "Manual check unavailable at $i."
        Require (-not ($o.SettingsButtons | Where-Object { $_ -and $_.Contains($download) })) "About still contains download at $i."
        Require (($o.SettingsButtons -contains $skip) -eq ($present -eq 1)) "Stale skip condition at $i."
        if ($present) {
            $b = $o.DownloadButton
            Require ($b.Loaded -and $b.Enabled -and $b.Geometry.AncestorsVisible -and $b.Geometry.ActualWidth -gt 0) "Button not mounted at $i."
            Require ($b.Geometry.X -ge 0 -and ($b.Geometry.X + $b.Geometry.ActualWidth) -le ($o.ClientDip.Width + 0.5)) "Button clipped at $i."
        }
    }
    Require ($observations[3].SkippedVersion -eq '99.0.0' -and $observations[3].PersistedSkippedVersion -eq '99.0.0') 'Skip not persisted.'
    $proxy = 'https://gh-proxy.org/https://github.com/gzy31007/TJDesktopTimetable/releases/download/'
    Require ($observations[2].DownloadUrl -eq ($proxy + 'v99.0.0/package-setup.exe')) 'Installer URL mismatch.'
    Require ($observations[6].DownloadUrl -eq ($proxy + 'v100.0.0/package-win-x64.zip')) 'Zip fallback URL mismatch.'
    Require ($observations[2].DownloadUrl -eq $observations[2].ExpectedDownloadUrl -and $observations[6].DownloadUrl -eq $observations[6].ExpectedDownloadUrl) 'Click URL differs from the current result.'
}
