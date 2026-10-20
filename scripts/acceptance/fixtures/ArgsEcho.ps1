# Prints its command line arguments as a JSON array. Used by tests to check that
# the runner passes arguments through without altering spaces, quotes, or
# trailing backslashes.
$parts = foreach ($value in $args) { ConvertTo-Json -InputObject ([string]$value) }
Write-Output ('[' + ($parts -join ',') + ']')
