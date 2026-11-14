# Everywhere Data & Privacy Policy

**Last updated:** October 2025

## Overview
Everywhere is an open-source application — all telemetry code is public and auditable.

Everywhere (“we”, “our”, or “us”) is committed to protecting your privacy.  
This Data & Privacy Policy explains how we collect, use, and safeguard information when you use our application (“App”).  
By using the App, you agree to the terms of this policy.

Everywhere diagnostic and telemetry data is **completely anonymous** and **never includes personal information or chat content**.  
We do not collect, store, or share any of your chat messages, files, API keys, email addresses, IP addresses, or any other personal identifiers.

Additionally, this document outlines how we handle data related to third-party services integrated into the App.

---

## Information We Collect

We collect only the minimum data necessary to ensure stability and improve the App.  
All data is transmitted securely over HTTPS and anonymized before upload.

1. **Usage (Optional)**  
  Helps us understand how often certain features are used so we can improve them.
  - Examples: App launches, settings toggled, or feature usage counts.
  - Users can disable this telemetry in **Settings → Common → Diagnostic data**.
  - When disabled, no usage or performance data will be uploaded.

2. **Stability (Required)**  
  Crash and error reports help us identify and fix common or critical issues.
  - Examples: Error messages, stack traces, OS version, App version.
  - These reports do **not** include user data, chat content, or identifiers.
  - Error reports are considered essential for maintaining application stability and cannot be disabled.

3. **Performance (Optional)**  
  Anonymous metrics about responsiveness and runtime performance help ensure a smooth experience.
  - Examples: App startup time, resource load durations, frame render timing.
  - Can be disabled together with usage telemetry in the same settings menu.

---

## User Control

You can control telemetry collection in the App:

- Go to **Settings → Common**
  - **Diagnostic data** → *On / Off*
  - When turned **Off**, only minimal error reports will be uploaded if a crash occurs.

We strive to make all data collection **transparent, minimal, and anonymous**.  
You will never be asked to provide consent for any personal information because the App does not collect any.

---

## Third-Party Services

Visible chat links can load the site's `/favicon.ico` without sending the link's path, query string, or fragment.
Hovering can request the linked page without its fragment and load a cover image. Links with a query string make no
automatic icon or preview requests; their hover card shows only the domain. Opening or copying still uses the full URL.
Requests use your configured proxy, do not send browser cookies or a referrer, and do not run website scripts.
Results are cached in memory. To stop these requests, turn off **Load link icons and previews** in the chat window settings.

We use **Sentry** for error and diagnostic data collection.
- Only anonymous event data (such as stack traces and version tags) is sent.
- We configure Sentry to automatically remove or scrub any potential sensitive data before storage.
- The DSN used in the App allows only error ingestion and does not expose project credentials.

Other third-party services (e.g., PostHog, optional analytics) may also receive anonymous, non-identifiable telemetry when enabled.  
All such integrations comply with the same principles of **minimality and anonymity**.

---

## Security Practices

- Everywhere services use HTTPS. Previews of HTTP links may use an unencrypted connection.
- No personal identifiers or user-generated content are ever included in telemetry.
- Before sending, data is filtered to remove file paths, local usernames, or other potentially identifying information.
- We regularly review our telemetry and error collection setup to ensure compliance with privacy best practices.

---

## Compliance and Transparency

We follow the principle of **“data minimization”** — collecting only what is necessary for functionality and stability.  
Even though error reporting cannot be disabled, it contains no personal data and serves only diagnostic purposes.

If you have any concerns or questions about privacy, please contact us:  
📧 **support@sylinko.com**

---

## Changes to This Policy

We may update this policy to reflect changes in functionality, legal requirements, or third-party services.  
When changes occur, the updated version and date will be published in the App and repository.

---
