---
name: dns-lookup
description: Perform DNS queries using PowerShell Resolve-DnsName to retrieve structured DNS records (A, AAAA, MX, TXT, NS, CNAME).
license: MIT
compatibility: Requires PowerShell 5.1+ or PowerShell Core with Resolve-DnsName available
metadata:
  author: phongnguyen
  version: "2.0"
allowed-tools: powershell
script_path: scripts/resolve-dns.ps1
---

# DNS Lookup Skill (PowerShell - Resolve-DnsName)

This skill performs DNS queries using the `Resolve-DnsName` cmdlet, returning structured and reliable DNS results.

## When to use

Use this skill when:
- The user wants to resolve a domain name
- The user needs specific DNS records (A, MX, TXT, etc.)
- The user is debugging DNS issues

## Inputs

- `Domain` (required, string): Domain name to resolve (e.g., example.com)
  - Validation: Must match regex `^[a-zA-Z0-9.-]+$`
- `RecordType` (optional, string): DNS record type to query
  - Valid values: A, AAAA, MX, TXT, NS, CNAME
  - Default: A
- `DnsServer` (optional, string): Specific DNS server to query against
  - Validation: Must be a valid IPv4 address format (e.g., 8.8.8.8)

## Instructions

1. The script validates all inputs:
   - `Domain` must contain only letters, numbers, dots, and hyphens
   - `RecordType` must be one of the allowed types (A, AAAA, MX, TXT, NS, CNAME)
   - `DnsServer` (if provided) must be a valid IPv4 address

2. If `RecordType` is not provided, it defaults to `A` (address records)

3. The script executes `Resolve-DnsName` with the validated parameters:
   - Uses the specified `Domain` and `RecordType`
   - Optionally queries a specific `DnsServer` if provided

4. Results are returned as JSON with the following fields:
   - `Name`: The domain name
   - `Type`: The record type
   - `TTL`: Time to live value
   - `IPAddress`: (for A/AAAA records) The IP address
   - `NameHost`: (for CNAME/NS records) The host name
   - `Exchange`: (for MX records) The mail exchange server
   - `Preference`: (for MX records) The preference value
   - `Text`: (for TXT records) The text content