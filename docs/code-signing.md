# Code signing

Releases are unsigned today. Windows then shows SmartScreen's "Windows protected your PC" for the installer
and the zip's DeviceDeck.exe until enough people have run that exact file, and some antivirus products are
more suspicious of unsigned apps that talk to USB devices. Signing fixes the "Unknown publisher" line at once;
SmartScreen reputation still builds over time (since 2024 even EV certificates no longer skip it).

The release workflow (`.github/workflows/release.yml`, step "Signing") signs as soon as one of the two setups
below is configured in the GitHub repository; nothing else has to change. It signs DeviceDeck's own .exe and
.dll files in the portable zip, and Velopack signs the installer, its updater and the update packages.

## Option 1: Microsoft Artifact Signing (formerly Azure Trusted Signing) — recommended

What it costs and needs:

- An Azure subscription and an **Artifact Signing account**: about USD 9.99 a month (Basic, 5,000 signatures a
  month; far more than releases need).
- **Identity validation** by Microsoft for the certificate profile. "Public Trust" for an individual or an
  organisation; Microsoft checks a government ID (individuals) or company records (organisations). Availability
  depends on the country of the person or company being verified; check the current list in Microsoft's docs
  before paying for anything.
- A Microsoft Entra **app registration** with a client secret, given the role *Artifact Signing Certificate
  Profile Signer* (formerly *Trusted Signing Certificate Profile Signer*) on the account.

Then in GitHub › Settings › Secrets and variables › Actions:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `AZURE_TENANT_ID` | the Entra tenant id |
| Secret | `AZURE_CLIENT_ID` | the app registration's client id |
| Secret | `AZURE_CLIENT_SECRET` | its client secret |
| Variable | `ARTIFACT_SIGNING_ENDPOINT` | the account's region endpoint, e.g. `https://weu.codesigning.azure.net` |
| Variable | `ARTIFACT_SIGNING_ACCOUNT` | the account name |
| Variable | `ARTIFACT_SIGNING_PROFILE` | the certificate profile name |

The workflow downloads Microsoft's signing client (`Microsoft.ArtifactSigning.Client` from NuGet) and uses it
with signtool. Certificates are short-lived and renewed by Microsoft; there is nothing to store or renew.

## Option 2: a code-signing certificate (.pfx)

Since June 2023 public CAs (DigiCert, Sectigo, GlobalSign, SSL.com, Certum…) only issue code-signing keys on a
hardware token or in their cloud HSM, so a plain .pfx file is mostly a thing of the past:

- An **OV code-signing certificate** costs roughly USD 200–500 a year, plus a token or a cloud-signing plan.
  A key on a USB token can't be used by GitHub's runners; a CA's cloud signing service (for example SSL.com
  eSigner) can, but needs its own tool instead of the .pfx step.
- An existing certificate that you do have as a .pfx works directly:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNING_PFX_BASE64` | the .pfx as base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) |
| Secret | `SIGNING_PFX_PASSWORD` | its password |

If both setups are present, Artifact Signing is used.

## Free for open source

If the repository is public with an OSI licence, **SignPath Foundation** signs open-source releases for free
through its own GitHub integration (it would replace the step above). The repository has no LICENSE file yet;
that would come first.

## Signing locally

`tools/package.ps1 -SignTemplate "<command with {{file}}>"` takes the same signing command the workflow builds,
for example `"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p <password> {{file}}`.
