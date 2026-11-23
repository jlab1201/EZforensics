# Security policy

## Supported versions

| Version | Supported |
|---|---|
| Latest release | Yes |
| Older releases | No |

## Report a vulnerability

- Do not open a public issue.
- Go to the **Security** tab of https://github.com/jlab1201/EZforensics.
- Click **Report a vulnerability** (GitHub private vulnerability reporting).
- Include the version, steps to reproduce and the impact.

You will get a reply as soon as the maintainer can. Please allow time for a fix before sharing details.

## What the app downloads

- Tools come from their official sources over https.
- Some downloads are pinned to a SHA-256 and rejected if it differs.
- Eric Zimmerman tools are checked by host, and their hash is logged. Each downloaded tool exe must also carry a valid Authenticode signature from Eric Zimmerman (ZimTech LLC), or it is not installed and the old version is kept.

## Running as administrator

- EZ Forensics installs per user, so anything running as your user could change its files.
- "Restart as administrator" starts the app's own exe from that folder. Only use it on a machine you trust.
- EZ tools are signature-checked before they are started as administrator. An unsigned or modified tool is refused.

## AI providers

- Local Ollama keeps your data on the machine. It is the default.
- Cloud providers (Anthropic, OpenAI, OpenRouter) are opt-in. They receive the data you share with the assistant, under that provider's terms.
