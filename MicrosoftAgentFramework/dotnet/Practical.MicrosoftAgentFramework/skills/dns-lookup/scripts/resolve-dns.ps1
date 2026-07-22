param(
    [Parameter(Mandatory = $true)]
    [string]$Domain,

    [ValidateSet("A","AAAA","MX","TXT","NS","CNAME")]
    [string]$RecordType = "A",

    [string]$DnsServer
)

# Validate domain
if ($Domain -notmatch '^[a-zA-Z0-9.-]+$') {
    Write-Error "Invalid domain format. Only letters, numbers, dots, and hyphens are allowed."
    exit 1
}

# Validate DNS server (basic IPv4 check)
if ($DnsServer -and $DnsServer -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
    Write-Error "Invalid DNS server format. Must be a valid IPv4 address."
    exit 1
}

try {
    # Build parameters safely
    $params = @{
        Name        = $Domain
        Type        = $RecordType
        ErrorAction = "Stop"
    }

    if ($DnsServer) {
        $params["Server"] = $DnsServer
    }

    # Execute DNS query
    $results = Resolve-DnsName @params

    # Transform to clean, consistent objects
    $output = $results | ForEach-Object {
        $record = [ordered]@{
            Name = $_.Name
            Type = $_.Type
            TTL  = $_.TTL
        }

        # Map record-specific fields
        if ($_.IPAddress) { $record["IPAddress"] = $_.IPAddress }
        if ($_.NameHost)  { $record["NameHost"]  = $_.NameHost }
        if ($_.Exchange)  { $record["Exchange"]  = $_.Exchange }
        if ($_.Preference) { $record["Preference"] = $_.Preference }
        if ($_.Strings)   { $record["Text"] = ($_.Strings -join " ") }

        [PSCustomObject]$record
    }

    # Return JSON (compressed for agents)
    $output | ConvertTo-Json -Depth 5 -Compress
}
catch {
    Write-Error "DNS query failed: $($_.Exception.Message)"
    exit 1
}