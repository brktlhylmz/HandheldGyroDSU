# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| `1.0.x` | :white_check_mark: |

## Network & Local IPC Architecture

**HandheldGyroDSU** is designed strictly for local loopback operation:
- Both the primary Cemuhook DSU UDP server (`26760`), optional secondary cluster server (`26762`), and local HTTP IPC control server (`26761`) bind exclusively to **`127.0.0.1` (`IPAddress.Loopback`)**.
- No telemetry, analytics, or external network requests are ever made by the application.

## Reporting a Vulnerability

If you discover a security issue, please open a discussion or report it via GitHub's private vulnerability reporting feature on this repository rather than posting an exploit publicly.
