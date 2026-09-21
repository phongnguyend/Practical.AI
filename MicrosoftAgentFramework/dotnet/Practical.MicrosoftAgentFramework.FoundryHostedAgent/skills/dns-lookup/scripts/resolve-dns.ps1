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
    $params = @{
        Name        = $Domain
        Type        = $RecordType
        ErrorAction = "Stop"
    }

    if ($DnsServer) {
        $params["Server"] = $DnsServer
    }

    $results = Resolve-DnsName @params

    $output = $results | ForEach-Object {
        $record = [ordered]@{
            Name = $_.Name
            Type = $_.Type
            TTL  = $_.TTL
        }

        if ($_.IPAddress) { $record["IPAddress"] = $_.IPAddress }
        if ($_.NameHost)  { $record["NameHost"] = $_.NameHost }
        if ($_.Exchange)  { $record["Exchange"] = $_.Exchange }
        if ($_.Preference) { $record["Preference"] = $_.Preference }
        if ($_.Strings)   { $record["Text"] = ($_.Strings -join " ") }

        [PSCustomObject]$record
    }

    $output | ConvertTo-Json -Depth 5 -Compress
}
catch {
    Write-Error "DNS query failed: $($_.Exception.Message)"
    exit 1
}
