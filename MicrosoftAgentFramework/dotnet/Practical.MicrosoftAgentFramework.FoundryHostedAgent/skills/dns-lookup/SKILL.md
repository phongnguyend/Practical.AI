---
name: dns-lookup
description: Perform DNS queries using a PowerShell script to retrieve structured DNS records (A, AAAA, MX, TXT, NS, CNAME).
license: MIT
compatibility: Requires PowerShell 7+ and the dig command
metadata:
  author: phongnguyen
  version: "2.0"
allowed-tools: powershell
script_path: scripts/resolve-dns.ps1
---

# DNS Lookup Skill (PowerShell)

This skill executes `resolve-dns.ps1` with PowerShell. The script uses `dig` as its cross-platform DNS backend and returns structured JSON results.

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
