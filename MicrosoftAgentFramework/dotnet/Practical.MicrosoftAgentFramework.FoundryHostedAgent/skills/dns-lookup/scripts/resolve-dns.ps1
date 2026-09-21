param(
    [Parameter(Mandatory = $true)]
    [string]$Domain,

    [ValidateSet("A","AAAA","MX","TXT","NS","CNAME")]
    [string]$RecordType = "A",

    [string]$DnsServer
)

if ($Domain -notmatch '^[a-zA-Z0-9.-]+$') {
    Write-Error "Invalid domain format. Only letters, numbers, dots, and hyphens are allowed."
    exit 1
}

if ($DnsServer -and $DnsServer -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
    Write-Error "Invalid DNS server format. Must be a valid IPv4 address."
    exit 1
}

try {
    $digArguments = @("+noall", "+answer", $Domain, $RecordType)
    if ($DnsServer) {
        $digArguments = @("@$DnsServer") + $digArguments
    }

    $answer = & dig @digArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($answer -join [Environment]::NewLine)
    }

    $output = @($answer | ForEach-Object {
        $line = $_.ToString().Trim()
        if (-not $line) {
            return
        }

        if ($line -notmatch '^(?<name>\S+)\s+(?<ttl>\d+)\s+(?<class>\S+)\s+(?<type>\S+)\s+(?<data>.*)$') {
            throw "Unexpected DNS response: $line"
        }

        $record = [ordered]@{
            Name  = $Matches.name.TrimEnd('.')
            Type  = $Matches.type
            TTL   = [int]$Matches.ttl
            Value = $Matches.data
        }

        if ($Matches.type -eq "MX" -and $Matches.data -match '^(?<preference>\d+)\s+(?<exchange>\S+)$') {
            $record["Preference"] = [int]$Matches.preference
            $record["Exchange"] = $Matches.exchange.TrimEnd('.')
        }

        [PSCustomObject]$record
    })

    ConvertTo-Json -InputObject $output -Depth 5 -Compress
}
catch {
    Write-Error "DNS query failed: $($_.Exception.Message)"
    exit 1
}
