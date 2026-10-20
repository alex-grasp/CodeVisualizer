# ReportKit

[![Build](https://img.shields.io/badge/build-passing-brightgreen.svg)](https://example.invalid/reportkit/actions) [![npm](https://img.shields.io/badge/npm-2.4.1-blue.svg)](https://example.invalid/package/reportkit) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

<img src="docs/logo.png" alt="ReportKit logo" width="120" align="right">

ReportKit is a small command-line tool that turns JSON report definitions into PDF, XLSX and CSV files. It has no server component and no database; everything runs locally.

## Features

- Renders PDF, XLSX and CSV from one definition
- Watches a folder and re-renders on change
- Ships as a single binary for Windows, macOS and Linux

## Installation

Requires Node 20 or later.

```bash
npm install -g reportkit
```

Or build from source:

```bash
git clone https://example.invalid/reportkit/reportkit.git
cd reportkit
npm install
npm run build
```

On Linux you can also use the Makefile:

```bash
make
make install
```

## Usage

```bash
reportkit export --format pdf ./reports/q3-summary.json
reportkit watch ./reports --out ./dist
reportkit --help
```

| Flag | Description |
|---|---|
| `--format` | `pdf`, `xlsx` or `csv` (default `pdf`) |
| `--out` | Output folder (default `./dist`) |
| `--watch` | Re-render when a definition changes |
| `-v, --verbose` | Print timing for each step |

## Configuration

ReportKit reads `reportkit.config.json` from the current folder when present:

```json
{
  "defaultFormat": "pdf",
  "outDir": "./dist",
  "locale": "en-GB"
}
```

Environment variables override the file: `REPORTKIT_OUT_DIR`, `REPORTKIT_LOCALE`.

## Contributing

1. Fork the repository and create a branch from `main`.
2. Run `npm test` and make sure everything passes.
3. Open a pull request with a short description of the change.

Please read [CONTRIBUTING.md](CONTRIBUTING.md) before you start.

## License

MIT - see [LICENSE](LICENSE). Copyright 2026 Example Corp.
