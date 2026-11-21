# Microsoft Store release for Ukraine and Romania

## Partner Center

1. Verify the publisher account and reserve the product name after checking name and asset rights. Copy the package name, package publisher and publisher display name from **Product identity**. Verify any requested seller or trader information for the actual publisher, especially the EU marketplace disclosure for Romania if applicable.
2. Create an MSIX submission. Under **Pricing and availability**, choose **Free**, clear the market selection, and select **Ukraine** and **Romania** only. Review the selection before submitting. For an initial pilot, use private audience distribution. Set public visibility once the release checks are complete. Complete the age rating questionnaire using the shipping build and answer any encryption and restricted-capability questions based on the actual release.
3. Fill the Ukrainian and Romanian listings using [listing.md](../store/listing.md). Add anonymized screenshots of the actual Windows build and a support contact. Keep the screenshots free of real pairing codes, personal files, and identifiers. The Microsoft Store package is a Windows app. The mobile interface is served by the PC to Safari.
4. Fill the [privacy template](../store/privacy.template.html) with the actual publisher, working email, effective date and accurate Ukrainian and Romanian support-data handling terms. Verify all placeholders were replaced, then host the page at a publicly accessible HTTPS URL without requiring sign-in. Put the same URL in the listing and the build command. If support or the privacy site processes personal data, verify the actual retention, hosting logs, recipients, legal basis and applicable rights before finalizing the disclosure.

[Create and submit an MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission) · [Market selection](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/market-selection) · [Microsoft Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies) · [Microsoft's EU trader verification guidance](https://learn.microsoft.com/en-us/windows/apps/publish/store-business-verification-reqs)

## Build and verify

Run from the project root with the real identity and live privacy URL supplied in the corresponding PowerShell variables. Use MakeAppx.exe from the Windows SDK.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-store.ps1 `
  -PackageName $packageName -Publisher $packagePublisher `
  -PublisherDisplayName $publisherDisplayName -PrivacyPolicyUrl $privacyUrl `
  -Version 1.0.0.0 -MakeAppxPath $makeAppxPath
```

- Upload the generated x64 MSIX. The Store handles distribution signing. Leave the package identity unchanged for future updates and increase the version. Leave the fourth version component at zero. The unpackaged EXE and ZIP are separate distributions. The local certificate and private keys are generated on each PC outside the package.
- Run Windows App Certification Kit against the actual installable Store build. On a clean Windows 11 x64 test PC, verify both languages with a real iPhone in Safari, including profile installation and fingerprint verification, first pairing, reconnect, every control in both themes, screen enable/disable, device revocation and receiver shutdown. Verify a private network works and a public network rejects new connections. Record the iPhone and iOS versions you tested.
- Verify Store startup is off after a clean install. Open the tray menu and use the Windows startup settings to opt in. Sign out and back in, confirm PocketPointer starts with no main window, then verify opt-out.
- Test a Store update and confirm the root fingerprint and pairing remain valid. Separately test migration from the existing EXE. Disable its Startup shortcut and exit the old app before starting the Store app so both versions cannot race for port 49732. Compare the fingerprint before removing the old installation. Remove old shortcuts only after confirming the Store installation works.
- Confirm uninstall and document any remaining local data. Check the public privacy URL from a clean browser. Submit only when the actual package and listing have both passed these checks.

[Certification and Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-certification-process) · [MSIX package creation](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool)

## Certification notes to paste into Partner Center

PocketPointer is a Windows companion app that pairs with an iPhone running Safari on the same trusted IPv4 LAN. Use a Windows 11 x64 PC with a Private network profile, and connect the iPhone to the same network. Client isolation can prevent discovery or connection. If Windows prompts for firewall access, allow Private networks only. PocketPointer does not modify firewall or network-profile settings.

Open PocketPointer from Start. On the phone, visit the setup address displayed on the PC. Compare the downloaded certificate's fingerprint with the fingerprint shown on the PC. Follow the on-screen instructions to install and trust the per-PC certificate on that iPhone. Open the HTTPS address shown by PocketPointer and use the pairing code visible on the PC. Test the touchpad, scroll, keyboard and volume in a standard Windows app, then enable and disable screen transmission from the phone. There are no demo accounts. To stop access, revoke the paired phone from PocketPointer or stop the receiver from its tray menu.

The package requests `runFullTrust` to run the desktop companion in the signed-in user's session. It uses the Windows desktop APIs for input, screen capture, and the notification-area icon. It runs with the signed-in user's permissions. The Store startup task is disabled by default and can be enabled in Windows Settings. The companion does not install a root in the Windows trust store.

## Publisher checks

- Confirm the rights to the name, icon, code and listing assets, including any material brought in from outside this repository. Verify support and publisher identity and the applicable trader obligation in Romania against the publisher's situation. [EU trademark availability](https://www.euipo.europa.eu/en/trade-marks/before-applying/availability)
- Confirm which Romanian and Ukrainian consumer and language obligations apply to the actual publisher and distribution model. The release supplies the Romanian and Ukrainian interfaces and setup instructions. [Romanian consumer protection ordinance](https://anpc.ro/wp-content/uploads/2025/05/Ordonanta-21-1992-privind-protectia-consumatorilor.pdf) · [Ukrainian language authority guidance](https://mova-ombudsman.gov.ua/informuvannya/derzhavna-mova-v-riznih-sferah/it-internet-tehnichna-dokumentaciya-dilovodstvo-trudovi-vidnosini)
- Determine whether GDPR and the EU Cyber Resilience Act apply to the publisher and this distribution. A zero price alone does not establish an exemption. If in scope, account for the CRA reporting obligations effective September 11, 2026, and the main requirements from December 11, 2027. Review applicable cryptography declarations for the publisher's location and markets. [GDPR](https://eur-lex.europa.eu/eli/reg/2016/679) · [Cyber Resilience Act](https://digital-strategy.ec.europa.eu/en/policies/cra-summary) · [Microsoft cryptography export guidance](https://learn.microsoft.com/en-us/windows/apps/develop/security/export-restrictions-cryptography)
