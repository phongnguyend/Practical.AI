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

- `Domain` (required, string): Domain name to resolve (e.g. example.com)
- `RecordType` (optional, string): A, AAAA, MX, TXT, NS, or CNAME. Defaults to A.
- `DnsServer` (optional, string): Specific IPv4 DNS server to query.

## Instructions

Run `scripts/resolve-dns.ps1` with the requested domain, record type, and optional DNS server. Return the structured JSON result.
