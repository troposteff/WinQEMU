# Microsoft Store

What WinQEMU needs on the Store, prepared but not submitted. The background is in
[`docs/distribution.md`](../../docs/distribution.md#microsoft-store).

| File | For |
|---|---|
| `identity.json` | The Store identity, copied from Partner Center (to create, see below). Read by `tools/Build-StorePackage.ps1`. |
| `listing.en.md`, `listing.fr.md` | Store listings: description, short description, features, search terms, links. |
| `certification-notes.md` | Notes for certification: QEMU, `runFullTrust`, elevated actions, optional drivers, how to test. |
| [`PRIVACY.md`](../../PRIVACY.md) | Privacy policy, its URL given in the listing. |

## Steps, all in Partner Center (https://partner.microsoft.com/dashboard), by the author

1. Create an individual developer account (free; identity check with a government ID and a selfie).
2. Apps and games, New product, MSIX or PWA app: reserve the name **WinQEMU**.
3. Product management, Product identity: copy the three values into `packaging/store/identity.json`:

   ```json
   {
     "Name": "<Package/Identity/Name>",
     "Publisher": "<Package/Identity/Publisher, CN=…>",
     "PublisherDisplayName": "<Package/Properties/PublisherDisplayName>"
   }
   ```

   They are public (they appear in every installed package): the file can be committed.
4. Build: `pwsh ./tools/Build-StorePackage.ps1`, which writes `artifacts\store\v<version>\WinQEMU_<version>_x64.msix`
   (unsigned: the Store signs it).
5. Start a submission:
   - Pricing and availability: free, the markets wanted.
   - Properties: category Developer tools; privacy policy URL; "This product accesses, collects, or transmits personal
     information": no.
   - Age ratings: the questionnaire (no user-generated content shared, no purchases).
   - Packages: the `.msix`.
   - Store listings: English (United States) and French (France), from the two listing files. Screenshots at least
     1366 x 768: `docs/images/summary-*.png` and `console-*.png` (1920 x 1150); the snapshot tree (850 x 830) is too small.
   - Submission options: restricted capability `runFullTrust`, the short block at the top of `certification-notes.md`
     (the field is short: the long notes did not fit); Notes for certification, the rest of that file.
6. Submit. Certification takes a few days; a refusal comes with the policy and the reason.

## After publication

- The README and the readiness assistant can point at the Store; WinGet installs it by its Store id (`msstore` source).
- PCs that had the GitHub version: WinQEMU from the Store takes their settings over at its first start and asks for the
  GitHub version to be uninstalled.
- Each version: raise `Version` in `Package.appxmanifest`, build, upload in a new submission.
