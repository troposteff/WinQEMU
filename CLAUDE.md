# WinQEMU

Interface graphique Windows (WinUI 3) au-dessus de QEMU, visant le confort de VMware ou Hyper-V :
bibliothèque de VM, assistant de création, console intégrée, instantanés, accélération WHPX.

## Structure

| Projet | Rôle |
|---|---|
| `WinQEMU.Core` | Tout ce qui ne dépend pas de l'interface : modèles (`VmConfig`…), construction de la ligne de commande (`QemuArgumentBuilder`), cycle de vie d'une VM (`VmSession`), QMP (`QmpClient`), `qemu-img`, détection de QEMU, interop Win32 de la console. |
| `WinQEMU` | Shell WinUI 3 empaqueté MSIX : `MainWindow`, view-models, dialogues, `VmConsoleHost` (console intégrée), `ClipboardBridge`. |
| `WinQEMU.Cli` | La commande `winqemu` (programme console du paquet) : transmet chaque commande à WinQEMU (voir Ligne de commande). |
| `WinQEMU.Core.Tests` | Tests xUnit du cœur. Il n'y a pas de tests du shell. |
| `tools/Generate-Logo.ps1` | Régénère les 64 PNG et l'ICO du logo depuis sa géométrie (voir plus bas). |

Une VM = un dossier sous la bibliothèque (`C:\qemu` par défaut) contenant son descripteur `<nom>.wqvm`
(JSON), ses disques et sa NVRAM UEFI (`firmware-vars.fd`). Les réglages de l'application
(`AppSettings`, dont le chemin de la bibliothèque) vivent à part, dans le stockage local du paquet, pour
qu'un dossier de VM reste déplaçable.

## Compiler, tester, déployer

```powershell
dotnet test WinQEMU.Core.Tests
dotnet build WinQEMU\WinQEMU.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

- Le shell est **MSIX uniquement** : `WinQEMU.exe` lancé directement plante. Le paquet est enregistré
  en place sur le dossier de compilation (`WinQEMU\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64`),
  donc **une recompilation est prise en compte sans réenregistrement**. Premier enregistrement :
  `Add-AppxPackage -Register <dossier>\AppxManifest.xml`, puis lancement par
  `shell:AppsFolder\cfde84db-a7f2-4241-ae31-4fd9bc34967b_6swaqgnpzyjaw!App` (éditeur `CN=troposteff` depuis la
  version 1.1 ; l'ancienne identité `…_xxm26tvjas0jr`, d'un autre éditeur, était une autre application, avec son
  propre stockage local).
- **WinQEMU ouvert verrouille ses fichiers** : la compilation échoue. Fermer l'app avant de déployer ;
  pour seulement vérifier que ça compile, ajouter `-p:OutDir=<dossier temporaire>\`.
- **Ne jamais faire `Remove-AppxPackage`** pour forcer un rafraîchissement : cela efface le stockage
  local du paquet, dont `settings.json`, et toutes les VM disparaissent de l'app. Si un retour de version
  oblige à désenregistrer, sauvegarder d'abord
  `%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local\WinQEMU\settings.json`.
- Méfiance envers un sous-dossier `AppX\` resté dans la sortie : l'enregistrer fait tourner du vieux code.
- **Changement du manifeste** (une capacité, par exemple) : il n'est lu qu'à l'enregistrement, et Windows refuse de
  réenregistrer la même version en mode développeur (0x80073CFB). Incrémenter `Version` dans `Package.appxmanifest`,
  compiler, puis `Add-AppxPackage -Register` : c'est une mise à jour en place, le stockage local est gardé.
- Après un déploiement, vérifier que la DLL installée contient bien le changement plutôt que de supposer.

## Paquet installable (`tools/Build-Package.ps1`)

`pwsh ./tools/Build-Package.ps1` écrit `artifacts\v<version>\` : le MSIX signé, `WinQEMU.cer`, `Install-WinQEMU.ps1` (à
lancer en administrateur sur le PC cible : approuve le certificat dans `TrustedPeople`, puis installe) et, pour une
version finale seulement, `WinQEMU.appinstaller`. **Monter `Version` du manifeste à chaque version publiée** : App
Installer ne met à jour que vers plus haut.

- **Numérotation** (choix de l'utilisateur, repartie de zéro le 29/10/2026 : releases 1.1.0.0 et 1.2.0.0 supprimées) :
  `2026.0.1_beta`, `2026.0.2_beta`… pour les fonctionnalités en cours, puis `2026.1.0` pour la version finale. **MSIX
  n'accepte que quatre nombres** : le manifeste porte `2026.0.1.0`, quatrième nombre toujours 0, et le suffixe n'existe
  que dans le nom affiché (Paramètres), le fichier, l'étiquette Git et le titre de la release. **Version mineure 0 = bêta**
  (`Core.AppVersion`, même règle dans le script).
- **Bêtas publiées en pré-versions GitHub** (choix de l'utilisateur) : `releases/latest` ne pointe jamais vers une
  pré-version, donc pas de mise à jour automatique ni de `.appinstaller` avant `2026.1.0` ; chaque bêta s'installe par son
  `.msix`. Le script affiche la commande `gh release create` (`--prerelease` pour une bêta).
- **Canal auto-signé abandonné le 07/11/2026** (choix de l'utilisateur) : les trois bêtas `2026.0.x` supprimées, leurs
  étiquettes aussi (09/11). Ses outils (`Build-Package.ps1`, `.appinstaller`, `Install-WinQEMU.ps1`) restent, inutilisés ;
  ce qui suit sur les bêtas et l'`.appinstaller` décrit ce canal tel qu'il marchait.
- **Releases GitHub = le paquet du Store et sa licence hors ligne, comme NanaZip** (choix de l'utilisateur, 09/11/2026, à
  partir de 2026.1.4) : `tools/New-GitHubRelease.ps1`, une fois la version publiée par le Store, télécharge `.msix` et
  licence (`winget download`, compte Entra administrateur), vérifie version et signature du Store, écrit
  `artifacts\github\v<version>\` (fichiers, `SHA256SUMS.txt`, `notes.md`) et **affiche** la commande `gh release create`,
  lancée seulement avec l'accord de l'utilisateur. Installation : `Add-AppxProvisionedPackage -Online -PackagePath …
  -LicensePath …` en administrateur (README). Précédents tiers : NanaZip (paquet + licence), Notepads, Ambie, Energy Star X
  (paquet seul).

- **Signé par un certificat auto-signé `CN=troposteff`** (magasin personnel de l'utilisateur, 5 ans ; partie publique
  dans `packaging\WinQEMU.cer`), choix de l'utilisateur. L'éditeur du manifeste doit être exactement ce sujet.
- **Windows App SDK embarqué** (`WindowsAppSDKSelfContained`) : aucun runtime à installer, seulement QEMU (98 Mo).
- **Jamais de réduction (`PublishTrimmed`)** en Release : `{Binding}`, les réglages et les descripteurs JSON passent par
  la réflexion.
- **Mises à jour par les releases GitHub** (canal abandonné, voir plus haut) : `.appinstaller` à l'adresse fixe
  `releases/latest/download/WinQEMU.appinstaller`, qui nomme le MSIX de sa release ; vérifié à chaque lancement, mise à
  jour en arrière-plan.
- **Vérifié dans Windows Sandbox** (Windows vierge, sans QEMU) : certificat approuvé, `Add-AppxPackage`, WinQEMU démarre,
  `winqemu help` répond.
- **Distribution** décrite en détail (en anglais, pour le dépôt public) dans `docs/distribution.md` : publication, canaux,
  signature, changement d'identité, Store, WinGet, vérifications manuelles. Points clés :
  - `tools/Get-WinQemuVersion.ps1` : la version lue du manifeste et son nom affiché, même règle que `Core.AppVersion` ;
    utilisé par `Build-Package.ps1` et par le workflow `release-check.yml`.
  - `SHA256SUMS.txt` écrit avec chaque paquet ; `Install-WinQEMU.ps1` vérifie le `.msix` contre lui **et** que le `.msix`
    est signé par le `WinQEMU.cer` qu'il va approuver, avant d'approuver quoi que ce soit (Personnes autorisées, jamais
    Racine).
  - **Release check** (`.github/workflows/release-check.yml`, sur les étiquettes `v20*`) : étiquette = version du manifeste,
    tests, paquet non signé en artefact. **Ne signe ni ne publie rien**, jeton en lecture seule : la clé reste sur le PC de
    l'auteur (choix de l'utilisateur), un tag poussé par un tiers ne peut produire aucun paquet signé.
  - Signature reconnue plus tard : toute option (Store, SignPath Foundation, certificat OV) change le `Publisher`, donc
    l'identité : une migration, comme le passage à `CN=troposteff` (1.1). Azure Artifact Signing exclut les particuliers hors
    États-Unis et Canada.
- **Installation sur un PC neuf** (essai de l'utilisateur sur une autre machine) : Windows refuse les scripts par défaut et
  `Unblock-File` n'y change rien ; les README donnent `Set-ExecutionPolicy Bypass -Scope Process`. **Smart App Control
  activé : le paquet s'installe mais WinQEMU ne démarre pas** (certificat auto-signé, hors du programme de racines de
  Microsoft). Seule issue de notre côté : une signature reconnue (Store, SignPath, OV). QEMU de weilnetz risque le même
  sort (`.exe` signés par un certificat expiré en 2023, DLL non signées), pas encore vérifié.
- **Microsoft Store : le seul canal**, produit `9NTWD738Z396`, publié (2026.1.4 au 09/11/2026 ; choix de l'utilisateur,
  plutôt que SignPath : éditeur affiché troposteff, mises à jour par le Store). `packaging/store/` (fiche FR/EN, notes de
  certification, étapes), `PRIVACY.md` (exigée par la capacité micro). Captures de la fiche : `docs/images/summary-*`,
  `console-*`, `wizard-*`, `snapshots-*` (1924 x 1130), mises en ligne par l'utilisateur dans Partner Center.
  - **Licence hors ligne pour les organisations activée** (Tarification et disponibilité, `EnterpriseLicensing` =
    `OnlineAndOffline`) : `winget download --id 9NTWD738Z396 -s msstore` donne le `.msix` signé par le Store (éditeur
    `CN=EA8415CD-…`) et, avec un compte Entra administrateur, `9NTWD738Z396_License.xml`. Vérifié dans Windows Sandbox :
    **sans licence, le paquet s'installe mais ne démarre pas** (« Impossible d'ouvrir cette application », `0x87E10BC6`) ;
    avec `Add-AppxProvisionedPackage -LicensePath`, WinQEMU démarre (le sandbox n'a pas le Store : un PC qui l'a obtiendrait
    peut-être la licence en ligne, comme le dit NanaZip ; non vérifié). Revérifié pour la release GitHub 2026.1.5
    (12/11/2026, sandbox **réseau coupé**, `.wsb` avec le dossier de la release monté en lecture seule et un script en
    `LogonCommand`, qui tourne élevé) : paquet provisionné avec sa licence, signature `Store`, WinQEMU démarre sur
    « Préparer ce PC », `winqemu help` répond (code 0). L'App Developer Agreement (8.11) ne dit rien de la
    republication ; la licence porte le `RequestorID` du tenant. Republiés sur GitHub (voir plus haut).
  - `tools/Build-StorePackage.ps1` : `.msix` **non signé** (le Store signe) avec l'identité de Partner Center, donnée au
    build par une copie du manifeste (`WinQemuStoreManifest` du csproj ; sans `AppxManifest` déclaré, les outils MSIX
    ajoutent eux-mêmes `Package.appxmanifest`). `.msix` plutôt que `.msixupload` : la tâche du Windows App SDK qui écrit ce
    dernier levait une exception masquée (System.Security.Permissions introuvable).
  - **Pas de pilote embarqué dans la version Store** (`WinQemuStore`, règle 10.2.4 : dépendre d'un pilote non Microsoft est en
    général refusé) : l'installateur TAP-Windows est **retiré** des éléments `Content` (le SDK les liste tous par défaut :
    une simple condition sur la copie le laissait dans le paquet, le script l'a vu). Réseau interne : lien vers l'installateur
    d'OpenVPN, comme Npcap (`TapDriver.IsBundled`). **Sans pilote, « Ajouter un réseau interne » est grisé** (demande de
    l'utilisateur : un bouton qui ne répondait que par un guide avait l'air de marcher), guide et lien affichés en
    permanence dessous ; le pilote est recherché toutes les 2 s tant que le dialogue est ouvert, et le bouton revient seul
    (`UpdateInternalAvailability`, lecture hors du fil de l'interface). Un pilote sans aucune carte ne compte qu'au bout
    de 15 s (`CanAddInternal`) : l'installateur écrit son dossier avant de créer la première carte, et un clic dans
    l'intervalle aurait demandé une élévation pour en ajouter une seconde (non reproduit : ce PC a le pilote). **De même « Ajouter un réseau externe » sans Npcap**, dans toutes les versions
    (`UpdateExternalAvailability`), son guide sous celui de TAP. L'aide sous les boutons a son texte propre à la version sans pilote
    (`NetworksTypesHintNoDriver` : elle disait le pilote « fourni avec WinQEMU » au-dessus du lien pour le télécharger).
    Vérifié dans l'interface (12/11/2026) sur un PC sans pilote TAP ni Npcap, l'installateur écarté du dossier de
    compilation (même chemin de code que la version Store, dont le `.msix` publié ne le contient pas) : les deux boutons
    grisés, leurs guides et liens, aucun réseau ajouté ; installateur présent, bouton interne actif et pas de guide TAP.
    Puis TAP-Windows installé par l'utilisateur depuis le lien, dialogue ouvert : bouton revenu et guide retiré tout
    seuls, réseau interne créé sans invite sur la carte de l'installateur ; Ubuntu branchée dessus par une seconde carte : ce PC a
    pris 10.77.1.1 par DHCP, l'invité 10.77.1.132, et répond au ping de ce PC. Même parcours pour Npcap : installé par
    l'utilisateur depuis le lien, bouton revenu seul, réseau externe créé sur la carte Ethernet, Ubuntu y a reçu
    192.168.1.113 de la box et joint la passerelle (ping depuis l'invité, par l'agent). Ce PC a désormais TAP et Npcap.
  - **Une autre identité = une autre application** : au premier démarrage sans réglages, `Services.SettingsImport` copie le
    `settings.json` de l'installation GitHub (famille `…_6swaqgnpzyjaw`, lu dans `%LOCALAPPDATA%\Packages\…\LocalCache`), et
    un bandeau demande de désinstaller celle-ci. Vérifié avec un paquet d'identité de test enregistré à côté (bibliothèque,
    dossiers, QEMU repris ; réglages d'origine inchangés), puis retiré.
- **Ce que Partner Center a refusé ou signalé, corrigé** : application `Cli` cachée (voir Ligne de commande) ;
  `Windows.Universal` en plus de `Windows.Desktop` (modèle WinUI : le Store l'aurait proposé sur Xbox) ; langues du paquet
  en `x-generate`, qui ne déclarait que l'anglais (le français est dans les assemblies satellites `.resx`, que MakePri ne
  voit pas) : `en-US` et `fr-FR` nommées. Justification de `runFullTrust` : champ court, texte de 311 caractères en tête
  de `certification-notes.md`. Fiches importables : `packaging/store/New-ListingImport.ps1` remplit l'export de Partner
  Center (ses colonnes Field, ID, Type doivent revenir intactes) et écrit le dossier à importer.
- **`systemAIModels` retirée** (2026.0.3) : reste du modèle de projet, jamais utilisée, et qu'il aurait fallu justifier au Store.
- **Mises à jour du Store : `tools/Publish-Store.ps1`** (produit `9NTWD738Z396`, publié ; première soumission faite à la main).
  Par `msstore` (Microsoft Store Developer CLI), connecté comme application Entra « WinQEMU Store CLI » du tenant
  `troposteff.onmicrosoft.com`, rôle Developer ; **secret gardé par msstore dans le Gestionnaire d'identification de ce PC,
  jamais dans le dépôt ni dans GitHub** (choix de l'utilisateur, comme la clé de signature). Brouillon (`publish --noCommit`
  sur le chemin du `.msix` : un dossier ferait reconnaître le projet WinUI), notes de version, description et fonctionnalités tirées des fiches, ancien paquet
  `PendingDelete`, soumission seulement sur « yes » de l'utilisateur. Pièges : `reconfigure` sans arguments demande un compte
  utilisateur (refuse les comptes Microsoft personnels) ; JSON de soumission modifié en `JsonNode` (ConvertFrom-Json
  changerait les dates). **Lancé pour 2026.1.4** : le rôle Developer suffit ; le paquet envoyé reste « PendingUpload » sans
  version tant que le Store ne l'a pas traité, d'où sa reconnaissance par nom de fichier, et un passage interrompu après
  l'envoi reprend le brouillon sans renvoyer le paquet. Le script est lancé par l'utilisateur, pas par Claude.
- **Déroulé d'une version** (`docs/distribution.md`, Releasing a version) : `Version` du manifeste et « What's new » des
  deux fiches, commit et push ; `Publish-Store.ps1` (l'utilisateur) ; attente de la certification et de la publication ;
  `New-GitHubRelease.ps1`, puis `gh release create` avec l'accord de l'utilisateur. L'étiquette est posée sur le commit
  courant : le script refuse si `WinQEMU`, `WinQEMU.Core` ou `WinQEMU.Cli` ont changé depuis le commit qui a fixé la
  version (le paquet du Store n'aurait pas été compilé de ces sources), ou si ce commit n'est pas poussé.

## Préparer ce PC (`Dialogs.ReadinessDialog`)

Ouvert au démarrage tant que QEMU ou l'accélération manque (sauf « Ne plus afficher au démarrage »,
`AppSettings.ReadinessDismissed`), depuis les Paramètres (« Vérifier ce PC… ») et le bandeau « QEMU introuvable ».

- **QEMU** : trouvé (version, dossier, architectures) ou la marche à suivre ; la commande winget est **copiée, jamais
  lancée**, et le lien mène au site des versions Windows : WinQEMU n'installe pas QEMU.
- **WHPX lu chez Windows** (`Services.HypervisorPlatform`, sans droits) : `WinHvPlatform.dll` absente → non activée ;
  présente et `WHvGetCapability(HypervisorPresent)` vrai → prête (ce que QEMU lui-même interroge) ; sinon, virtualisation
  du firmware (`IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED)`) éteinte → BIOS/UEFI, allumée → redémarrage (ou
  hyperviseur empêché par bcdedit). Le firmware n'est cru que si la fonctionnalité est là et qu'aucun hyperviseur ne tourne :
  Hyper-V seul le masque.
- **Bug corrigé au passage** : `QemuInstallation.SupportsWhpx` ne regardait que la compilation de QEMU (`-accel help`). Sur un
  PC sans la fonctionnalité, « Automatique » demandait WHPX et la machine ne démarrait pas, là où l'émulation l'aurait fait
  tourner. Désormais `WhpxInBuild && Platform == Ready`.
- **Activer** : `dism /Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart` par `Services.Elevation`
  (une invite UAC, sur clic de l'utilisateur) ; 0 ou 3010 = fait, redémarrage nécessaire, et c'est dit.

## Décisions d'architecture

Prises avec l'utilisateur ; ne pas les remettre en cause sans raison nouvelle.

- **Console : fenêtre SDL de QEMU rattachée à l'app.** QEMU tourne avec `-display sdl,gl=off`,
  le shell retrouve son HWND et le `SetParent()` dans sa fenêtre. VNC et SPICE ont été écartés **pour
  l'affichage** (pas de client .NET mûr, un client RFB complet serait à écrire).
- **Clavier par QMP, jamais par la fenêtre.** Une fenêtre SDL enfant n'atteint jamais l'état de focus
  qu'exige QEMU. `VmConsoleHost` capte les touches en XAML et les envoie en scancodes AT set 1
  (`input-send-event`). La souris passe nativement, **sauf image mise à l'échelle** : QEMU y vise faux, et
  WinQEMU envoie alors lui-même la position par QMP (`PointerOverlay`, décision de l'utilisateur). Ne jamais donner le focus Win32 à la fenêtre SDL :
  XAML cesserait de livrer les touches.
- **Console d'une VM non affichée : garée, pas rendue au bureau.** Rendue au bureau, SDL la réaffiche
  seul au premier changement de mode vidéo de l'invité. Elle est mise en enfant d'une fenêtre cachée de
  l'app et reprise telle quelle au retour (`QemuConsoleWindow.Park/Unpark`).
- **Presse-papiers : VNC utilisé comme simple canal de presse-papiers** (texte uniquement). L'interface
  SDL de QEMU n'a aucun support du presse-papiers, et le serveur VNC est le seul pair de presse-papiers
  côté hôte dans le build Windows. Côté invité : chardev `qemu-vdagent` sur le port virtio-serial
  `com.redhat.spice.0` (il faut `spice-vdagent` dans l'invité, et une session graphique). Côté hôte :
  `-vnc 127.0.0.1:N,password=on`, mot de passe aléatoire posé par QMP ; **`mouse=off`** sur le chardev (sinon l'agent
  prend le pointeur dès que spice-vdagent tourne, et les événements envoyés par QMP, sans écran d'origine, n'arrivaient
  plus à un Windows muni des outils virtio-win : vérifié par un clic QMP sur le menu Démarrer, avec et sans) ; `VncClipboardClient` ne parle
  que les messages *Extended Clipboard* et ne demande jamais d'image. `ClipboardBridge` synchronise avec
  Windows.
- **QEMU : détecter et guider, jamais embarquer ni télécharger.** `QemuLocator` explore et explique les
  échecs ; l'utilisateur peut désigner un dossier.

## Langues

Anglais et français (choix de l'utilisateur), **langue de Windows par défaut**, liste « Langue » dans les Paramètres
(`AppSettings.Language` : vide, `en`, `fr`). `Program.ApplyLanguage` pose `CultureInfo.DefaultThreadCurrentUICulture`
(`Text.UiLanguage`) avant toute fenêtre, et `ApplicationLanguages.PrimaryLanguageOverride` pour les quelques mots de
WinUI. Toute autre langue de Windows → anglais. Le format des nombres et des dates reste celui de Windows.

**Changement à chaud, comme le thème** (demande de l'utilisateur) : `MainViewModel.SelectedLanguage` applique la langue,
relit ses textes (`RefreshText` : `PropertyChanged` pour toutes les propriétés, lignes de la bibliothèque, matériel,
instantanés, statut de QEMU, ordre de démarrage), puis `MainWindow.ApplyLanguage` : `Bindings.Update()` (tous les
`{x:Bind t:…}` sont à usage unique), info-bulles, liste de mise à l'échelle, texte d'attente de la console, arbre et
graphiques redessinés, et **modèles d'éléments retirés puis remis** (`RefreshItemTemplates`) : les textes d'un disque ou
d'une carte sont liés par élément, hors de portée de `Bindings.Update`. La liste de la bibliothèque n'est pas reconstruite
(elle perdrait la sélection, donc la console). Les Paramètres ouverts suivent aussi. Pièges :

- **Jamais `CultureInfo.CurrentUICulture = …`**, seulement la valeur par défaut des fils : la première est une valeur
  async-local, et ce qui tournait déjà (une boucle qui attend son prochain tour) gardait l'ancienne langue.
- **Langue de Windows lue dans un constructeur statique explicite** (`UiLanguage`) : un simple initialiseur de champ n'est
  exécuté qu'à la première lecture du champ, soit après qu'« English » a été posé, et « Langue de Windows » ramenait l'anglais.
- **Nom accessible d'une section** (`Expander`) calculé une fois : reposé à la main, sinon Narrateur lisait « Disques »
  sous « Disks ».
- Les textes gardés dans des objets (étiquettes de connexion réseau, intervalles des instantanés automatiques) sont
  calculés à chaque lecture : remplacer la liste viderait la sélection, donc le réglage lié en deux sens. **Et ils
  signalent leur changement** (`RefreshText`) : la boîte d'une ComboBox ne relit l'élément sélectionné qu'alors —
  « Au plus une fois par jour » restait en français sous une interface anglaise.
- Vérifié dans l'app : FR → EN → FR et retour à la langue de Windows, Paramètres ouverts, toutes les sections du résumé,
  la console ; aucun descripteur de machine réécrit.

- **Textes en `.resx`** : anglais dans `Text\X.resx` (langue neutre, `NeutralLanguage` en), français dans
  `Text\X.fr.resx` (assembly satellite `fr\`, présent dans le paquet). Cœur : `CommonText` (public : OK, Annuler,
  unités…), `CoreText`, `PlatformText` ; shell : `WindowText` (MainWindow.xaml), `ShellText` (son code et
  `MainViewModel`), `DialogText` (le reste). **Une classe générée par fichier anglais** (`GenerateResource`,
  métadonnées `StronglyTyped*` du csproj) : une clé mal écrite ne compile pas, en C# comme en XAML.
- **XAML** : `{x:Bind t:WindowText.Cle}` (`xmlns:t="using:WinQEMU.Text"`, `ct` pour `WinQEMU.Core.Text`). **Piège** :
  le compilateur XAML passe avant `ResGen`, et une liaison vers une classe pas encore générée faisait sortir
  `XamlCompiler.exe` en erreur **sans message** : cible `GenerateTextBeforeXaml` du csproj. x:Bind est posé au
  `Loading` du dialogue : un texte que le constructeur fixe lui-même n'a pas de liaison XAML (il serait écrasé).
- **Ajouter des textes** : `python tools/add-text.py WinQEMU/Text/X.resx entrées.json` écrit chaque clé dans `X.resx` et
  `X.fr.resx` (`[{"key", "en", "fr"}]`, mêmes `{0}` exigés).
- **Tailles** : `Text.Sizes.Format` (« 2,4 Go » / « 2.4 GB »).
- **Tests épinglés en français** (`TextTests.PinFrench`) ; `TextTests` vérifie que chaque clé existe dans les deux
  langues avec les mêmes `{0}`.
- **Ce que WinQEMU crée dans l'invité suit la langue, mais un invité déjà configuré garde ses noms** : `~/Partages`
  ou `~/Shares` (le script de montage note le choix dans `/var/lib/winqemu/shares-folder` et reprend un dossier
  existant de l'autre langue), lecteur « Partages WinQEMU » ou « WinQEMU Shares » (retenu dans
  `HKLM\SOFTWARE\WinQEMU\SharesLabel`). Le dossier rapporté par l'invité est gardé dans `VmConfig.GuestSharesFolder`
  pour que chaque ligne de dossier partagé le montre (sinon WinQEMU anglais affichait `~/Shares/Music` pour un Ubuntu
  qui a `~/Partages`). Restent en français, car retrouvés par leur nom : la règle de pare-feu et la tâche
  `WinQEMU Partages` de l'invité.
- **Messages des programmes invités** (dépôt, récupération) : codes courts (`#notfound <chemin>`…) traduits côté hôte
  (`GuestFileDrop.GuestMessage`), pour que le programme invité ne dépende pas de la langue.
- **Scripts pour un invité Linux : `ReplaceLineEndings("\n")` là où ils sont définis.** Un littéral brut prend les fins
  de ligne du fichier source ; avec `core.autocrlf=true`, une copie de travail en CRLF donnait `set -u\r` à `/bin/sh`
  (« set: Illegal option - ») et aucun dossier partagé n'était monté (`LinuxScriptTests`).
- Vérifié dans l'app en anglais et en français ; un Ubuntu configuré en français garde `~/Partages` sous WinQEMU
  anglais.

## Instantanés

Trois formes, car WHPX interdit la seule que l'utilisateur attendrait (un instantané mémoire d'une
machine accélérée). L'interface (`VmSnapshotsViewModel`) ne propose que celles possibles dans l'état
courant, et dit pourquoi les autres ne le sont pas.

| Forme | Quand | Mécanisme | Capture |
|---|---|---|---|
| **Hors ligne** | machine arrêtée | `qemu-img snapshot -c/-a/-d` sur chaque disque qcow2 (`QemuImgService`) | disques seulement |
| **À chaud, disques** | machine en marche, tout accélérateur | QMP `blockdev-snapshot-sync` : chaque disque reçoit une superposition qcow2 (`<id>-<nom>.qcow2`), l'image précédente devient son image de base (`VmSession.CreateLiveDiskSnapshotAsync`) | disques, cohérence de type coupure de courant |
| **Mémoire** | machine en marche **sous TCG uniquement** | `savevm` / `loadvm` par le moniteur humain (`VmSession.SaveSnapshotAsync`) | disques + RAM + état CPU |

- **Pourquoi pas de mémoire sous WHPX** : `savevm` repose sur la migration, que WHPX n'implémente pas.
  Un instantané « mémoire » pris quand même se restaurerait sur une machine plantée ; d'où le refus
  explicite (`SupportsMemorySnapshots`).
- **Un instantané = un nom partagé par plusieurs disques.** `VmSnapshotAggregator` regroupe les
  instantanés internes de chaque qcow2 par nom. Un instantané absent de certains disques est signalé
  **partiel** : le restaurer laisserait les disques incohérents entre eux. Les disques non qcow2 (raw,
  vhdx…) ne peuvent pas porter d'instantané ; ils sont listés à part pour que l'utilisateur ne se croie
  pas protégé.
- **Restaurer ≠ reprendre.** Un instantané avec mémoire se **reprend** par `loadvm` dans une machine
  TCG en marche. L'appliquer avec `qemu-img` restaurerait le disque en ignorant la mémoire, et l'invité
  repartirait comme après une coupure.
- **qemu-img refuse de toucher une image ouverte** par un QEMU en marche, et forcer corromprait le
  disque : les instantanés hors ligne ne sont manipulables que machine arrêtée.
- **Noms en ASCII uniquement** (`QemuImgService.ValidateSnapshotName`). qemu-img reçoit ses arguments
  via la page de code ANSI sous Windows : un « à » est stocké comme `U+FFFD`, quel que soit l'encodage
  envoyé. Vérifié en passant des octets UTF-8 corrects depuis un shell.
- **Superpositions des instantanés à chaud** : la machine écrit déjà dans la superposition, donc le
  descripteur est mis à jour aussitôt (`FileName` = superposition, `DiskConfig.BackingFileName` = image
  de base), sinon le démarrage suivant rouvrirait l'ancienne image et perdrait tout ce qui a été écrit
  depuis. La relation est **enregistrée**, pas redécouverte par `qemu-img info`. Machine arrêtée, la
  section Disques propose **Revenir à l'image de base** (supprime la superposition : tout ce qui a été
  écrit depuis est perdu ; le descripteur est repointé avant la suppression) ou **Fusionner**
  (`qemu-img commit` : on garde tout, l'instantané disparaît).
- **Les instantanés à chaud figurent dans la liste.** Ce ne sont pas des instantanés internes, donc lire
  les images ne les trouve jamais : `VmSnapshotAggregator.LiveSnapshots` les reconstitue depuis les
  chaînes du descripteur, un instantané par niveau, nommé d'après le fichier `<id>-<nom>.qcow2`
  (`DiskConfig.SnapshotNameOf`). La superposition d'un instantané contient ce qui a été écrit **après**
  lui ; l'état à cet instant est l'image du dessous. D'où, machine arrêtée (`LiveSnapshotOperations`) :
  **Restaurer S** dépile la superposition de S et toutes celles au-dessus ; **Supprimer S** (en gardant les
  données) fait `qemu-img commit` de sa superposition vers le bas puis, s'il y a un niveau au-dessus, un
  `rebase -u` de celui-ci sur l'image du dessous — correct parce que le commit vient d'y mettre les mêmes
  données. Testé sur de vrais fichiers avec un motif distinct par niveau (`LiveSnapshotOperationTests`).
- **Disques non qcow2 exclus des instantanés à chaud** aussi : le descripteur n'a qu'un format par
  disque, il nommerait la superposition qcow2 « raw » et le démarrage suivant donnerait ses octets bruts
  à l'invité.
- **Superpositions empilées** : plusieurs instantanés à chaud de suite forment une chaîne.
  `BackingFileName` est l'image de base immédiate, `DeeperBackingFiles` les suivantes (la plus proche
  d'abord) — deux propriétés plutôt qu'une liste pour que les descripteurs antérieurs se chargent tels
  quels. Ne jamais modifier ces champs à la main : passer par `DiskConfig.PushOverlay` (instantané à
  chaud) et `PopOverlay` (retour à la base ou fusion). Chaque niveau se défait séparément ;
  `qemu-img commit` écrit dans la base **immédiate** de la superposition, vérifié sur une chaîne de trois
  niveaux. Avant ce suivi, revenir sur le niveau du haut laissait celui du dessous sans base enregistrée.

- **Arborescence** (`SnapshotTree`, dessinée par `Controls.SnapshotTreeView` **de haut en bas, comme une arborescence de
  fichiers** : une ligne par nœud dans l'ordre en profondeur, un retrait par niveau ; de gauche à droite comme chez VMware,
  elle débordait du résumé dès trois niveaux) :
  « État d'origine », les instantanés sous celui dont ils partent, « Vous êtes ici », les clones liés et les
  branches conservées pour eux (grisées). Filiation des instantanés à chaud lue dans la chaîne (la superposition
  de S repose sur celle du précédent ; la machine écrit dans celle du haut). **qcow2 ne retient aucun parent pour
  ses instantanés internes** : WinQEMU l'enregistre lui-même à chaque prise (`VmConfig.SnapshotLinks`) et note
  où l'on se trouve après une restauration ou une reprise (`VmConfig.CurrentSnapshot`, vidé après une restauration
  à chaud : la chaîne le dit alors). Un parent enregistré l'emporte s'il existe encore ; les instantanés plus
  anciens que l'enregistrement pendent de l'origine ; une filiation en boucle (descripteur modifié à la main) est
  ignorée. Un clone lié lit toute une image, donc l'état où elle a été figée : il pend de l'instantané dont la
  superposition repose sur sa base (« Base de <clone> »), ou de celui de l'image s'il n'y a plus rien dessus.

- **Instantanés automatiques** (`Services.AutoSnapshots`, comme AutoProtect de VMware ; section Instantanés :
  intervalle « à chaque démarrage » … hebdomadaire, nombre gardé) : `Auto aaaa-MM-jj HHhmm` (ASCII, trié par date).
  **Pris au démarrage, hors ligne** (`MainViewModel.StartMachineAsync`, avant QEMU), pas pendant que la machine
  tourne : sous WHPX le seul instantané possible alors est une superposition, qui ne se fusionne que machine arrêtée ;
  une prise toutes les heures d'une longue session allongerait la chaîne sans moyen de l'élaguer. Les plus anciens
  au-delà du nombre gardé sont supprimés, **seulement ceux nommés `Auto …`**. Filiation enregistrée comme une prise
  manuelle. Un échec est signalé sans empêcher le démarrage. Vérifié dans l'app sur Debian (journal + `qemu-img
  snapshot -l`).

## Compactage des disques

Bouton **Compacter…** d'un disque, machine arrêtée (`Services.DiskCompaction`, comme « Compacter » de VMware) : un
qcow2 ne fait que grossir, même quand l'invité supprime des fichiers. `qemu-img convert` vers `<disque>.compact` à côté
(même volume), qui omet les clusters lus comme zéros ou rendus, puis `File.Replace`. Copie supprimée en cas d'échec.

- **Refusé** (`WhyNot`, raison affichée) : disque non qcow2 ; superposition d'instantané à chaud ou disque d'un clone
  lié (convert lirait toute la chaîne dans un seul fichier : instantané ou lien perdus) ; image protégée par un clone
  lié ; **instantanés internes** (convert ne les copie pas — les instantanés automatiques comptent) ; place libre
  insuffisante sur le volume.
- **La place des fichiers supprimés n'est rendue que si l'invité l'a signalée** : bouton « Signaler l'espace libre
  (TRIM) » des Outils invités (`guest-fstrim` par l'agent, machine en marche), qui prévient si un disque n'a pas
  l'option **Discard** (sans elle, les TRIM n'atteignent pas l'image).
- Vérifié sur le disque Debian de l'utilisateur, depuis l'app : 2,4 Go → 1,5 Go, `qemu-img compare` « Images are
  identical ». TRIM : 21 systèmes de fichiers sous Ubuntu, 1 sous Windows 11.

## Clonage

Deux formes, toujours machine source arrêtée (pause comprise : `VmManager` refuse).

### Copie complète (`VmLibrary.CloneAsync`)

- **Disques recopiés par `qemu-img convert`, jamais comme fichiers.** Une superposition d'instantané à
  chaud désigne sa base par **chemin absolu** (QEMU l'écrit ainsi) : copiée telle quelle, elle lirait
  encore l'image de l'original, et le clone changerait quand l'original fusionne un instantané.
  `convert` lit toute la chaîne et écrit une image autonome ; les instantanés ne suivent pas (ils
  décrivent le passé de l'original). Même format que la source.
- Les disques attachés **hors du dossier** de la VM sont copiés aussi : deux machines sur une même
  image inscriptible la corrompent.
- Nouvel `Id`, nouvelles MAC (une MAC vide reste vide), NVRAM UEFI copiée (entrées de démarrage).
  L'identité de l'invité (nom d'hôte, machine-id, SID) n'est pas touchée : le dialogue le dit.
- Échec ou annulation : le dossier du clone, alloué pour l'occasion, est supprimé. `ProcessRunner`
  attend la fin réelle de `qemu-img` tué, sinon ses fichiers restent verrouillés.
- Progression : `qemu-img convert -p` écrit `(12.34/100%)` séparés par `\r`, y compris sortie
  redirigée ; `QemuImgService.ParseProgress`, pondéré par la taille virtuelle de chaque disque.
- Copier un clone lié en entier donne une machine indépendante (`LinkedBaseFileName` vidé).

### Clone lié (`VmLibrary.PrepareLinkedCloneAsync` + `VmManager.CreateLinkedCloneAsync`)

Chaque disque du clone est une superposition qcow2 vide posée sur l'image **où l'original écrit à cet
instant** (F). Le clone lit dans F tout ce qu'il n'a pas écrit : **un seul octet changé dans F corrompt
le clone sans aucune erreur**. Tout le dispositif sert à ce que F ne bouge plus.

- **L'original est figé** : il reçoit lui aussi une nouvelle superposition, un instantané à chaud
  ordinaire nommé `Base de <clone>` (ASCII replié, unique), et n'écrit plus jamais dans F. Disques qcow2
  exigés des deux côtés (`WhyNoLinkedClone`), pour la même raison que les instantanés à chaud.
- **Le lien est enregistré côté clone seulement** : `DiskConfig.LinkedBaseFileName`, relatif au dossier
  du clone. **Pas `BackingFileName`** : celui-là marque une superposition que l'application sait dépiler
  ou fusionner, ce qui écrirait dans F. La chaîne du clone s'arrête donc sur sa propre image.
- **Chemins relatifs** des deux côtés (qemu résout une base relative depuis le dossier de la
  superposition) : déplacer toute la bibliothèque garde le couple valide (testé). Déplacer un seul des
  deux dossiers casse le clone : l'interface le dit.
- **Ordre d'enregistrement** : fichiers d'abord, puis superposition poussée sur l'original et
  descripteur de l'original, **puis** descripteur du clone. Interrompu entre les deux, l'original a
  juste un niveau de plus ; l'ordre inverse laisserait un clone lire une image encore inscriptible.
  En cas d'échec : superpositions dépilées, fichiers et dossier du clone supprimés.
- **Images protégées** (`LinkedCloneProtection`) : toute image qu'un clone lit, directement ou par ce
  sur quoi elle repose. La ligne se suit par les chaînes de tous les disques **et** par les images
  conservées (`DiskConfig.RetainedImages`, voir plus bas), recalculée depuis les descripteurs à chaque
  question : rien n'est stocké côté original sur ses clones, donc rien ne se périme.
- **Comme VMware : restaurer reste possible, supprimer non.**
  - **Supprimer** un instantané à chaud ou **Fusionner** (superposition `i` fusionnée dans `i+1`) :
    refusé si `i` ou `i+1` est protégée (`LinkedCloneGuard`). Aussi refusés : supprimer les fichiers
    d'une VM dont le dossier contient une image protégée, attacher une image protégée comme disque.
  - **Restaurer** / **Revenir à l'image de base** (`LiveSnapshotOperations.RevertAsync` /
    `RevertTopAsync`) : si l'état visé ou une couche à jeter est protégé, la machine **repart d'une
    nouvelle superposition** posée sur l'état visé au lieu d'écrire dedans. Les couches au-dessus de
    toute image protégée sont supprimées comme d'habitude ; les couches protégées sortent de la chaîne
    mais restent sur disque, notées dans `RetainedImages` **avec leur base** (plus aucune chaîne ne la
    donne). La nouvelle couche reprend le nom de l'instantané s'il est libre, sinon `nom (2)` — même
    suffixe sur tous les disques pour qu'ils restent groupés. Créée sous un nom temporaire avant que le
    descripteur change, puis renommée.
  - **Libération** (`VmManager.ReleaseRetainedImages`) : à la suppression **avec fichiers** d'un clone,
    ou quand « Convertir… » le rend indépendant, les images conservées qu'il lisait et que plus aucun
    clone listé ne lit sont supprimées. Limité à ce que ce clone lisait, jamais un balayage de toute la
    bibliothèque : un clone seulement retiré de la liste existe encore sur disque, et un balayage
    déclenché ailleurs détruirait ce qu'il lit. « Retirer de la liste » ne libère rien.
- `LiveSnapshotOperations` n'utilise pas `ConfigureAwait(false)` : les disques sont liés à l'interface
  et changer de chaîne lève `PropertyChanged`, qui doit rester sur le thread appelant.
- **Limite assumée** : un clone que la bibliothèque ne liste pas (retiré de la liste, ou hors du
  dossier de la bibliothèque) ne protège rien. Le dialogue de suppression d'un clone lié le signale.
- **Rendre un clone indépendant** : « Convertir… » sur son disque (lit la chaîne entière, vide
  `LinkedBaseFileName`), sauf si ce disque porte des superpositions : sa chaîne enregistrée finit alors
  encore sur l'image qui lit F, le lien est gardé. Ou bien faire une copie complète du clone.

## Dossiers de la bibliothèque

Comme l'inventaire de VMware Workstation : des dossiers imbriqués, vue de l'application sur ses machines, **pas
une propriété des machines** : `AppSettings.Library` (`Models.LibraryTree`), jamais dans les `.wqvm`. Arbre à plat
(chaque dossier nomme son parent), dossier de chaque machine par `VmConfig.Id`, état ouvert/fermé, tri.

- **Liste = ListView de l'arbre aplati** (`Controls.LibraryListView`, `MainViewModel.Library.cs`), pas un TreeView :
  toute l'app dépend de la sélection de la liste (console, barre d'outils, résumé), et celle du ListView marchait.
  `Rows` est mise à jour **en place** (`LibraryLayout.Synchronize`) sans jamais retirer la ligne sélectionnée :
  vider la liste fait perdre la sélection au ListView, donc détache la console, à chaque dépliage ou changement
  d'état trié par état. `SelectedItem` reste la seule entrée « machine » ; un dossier sélectionné = aucune machine.
- Un dossier qui n'existe plus → racine ; machines supprimées oubliées (à la suppression, et au chargement **sauf
  si le dossier de la bibliothèque est introuvable** : un disque débranché aurait tout désaffecté).
- Supprimer un dossier ne supprime jamais une machine : son contenu remonte au parent (sous-dossier homonyme
  numéroté). Noms uniques entre frères, insensibles à la casse. Pendant une recherche : résultats à plat avec leur
  chemin, dossiers masqués. Nouvelle machine (création, clone, import) : dans le dossier courant.
- Accessibilité : motif développer/réduire et niveau sur chaque ligne (`LibraryListViewItemPeer`), nom « Dossier
  Travail, 3 machines ».
- **Un dossier comme un tout** (comme les « teams » de VMware ; `MainViewModel.Folders`, résumé du dossier, son menu dans
  la liste, `winqemu start|stop|pause|resume --folder <chemin>`) : ses machines **et celles de ses sous-dossiers**
  (`LibraryTree.MachinesIn` : celles du dossier, puis chaque sous-dossier dans l'ordre de l'arbre, par nom). Démarrage dans
  l'ordre et avec les délais du démarrage automatique de chacune, comptés depuis le clic (`AutoStartPlan.ForGroup` : toutes
  les machines, cochées « Démarrer avec WinQEMU » ou non, modèles exclus) ; un seul dossier à la fois, annulable entre deux
  machines, l'heure de la suivante affichée ; un échec est dit et les suivantes démarrent. Arrêt (bouton d'alimentation)
  dans l'ordre inverse, une machine en pause reprise d'abord. **Depuis la ligne de commande, personne ne tape de mot de
  passe** : une machine chiffrée démarre avec son mot de passe mémorisé, sinon elle est laissée et c'est dit. Vérifié dans
  l'app et par la commande sur des machines jetables (seconde machine 15 s après la première, pause, reprise, arrêt,
  annulation, dossier inconnu → code 1).

### Modèles (`VmConfig.IsTemplate`)

Comme les modèles de VMware et Hyper-V : bouton **Modèle** du résumé (machine arrêtée, confirmation), puis **Déployer** à
la place de Démarrer, qui ouvre le dialogue de clonage sur un clone lié. Un modèle **ne démarre jamais** (`VmSession.StartAsync`
et `MainViewModel.StartMachineAsync`, avant l'instantané automatique ; démarrage automatique ignoré) et ses instantanés
sont figés (section et commande `winqemu snapshot`). « Reconvertir en machine virtuelle » le rend normal ; les machines
déployées continuent de marcher.

- **Une seule couche pour tous les clones**, pas une par clone : le premier clone lié fige le modèle comme toute machine
  (superposition nommée `Modele` / `Template`, pas d'après le clone), notée dans `DiskConfig.TemplateOverlayFileName` ; tant
  que le disque écrit encore dans cette superposition (`TemplateOverlayIsTop`), les clones suivants lisent l'image du
  dessous sans rien empiler (`VmLibrary.ReusesTemplateBase`). Sans cela, le Nᵉ clone lirait N niveaux.
- **Repasser en machine efface ce repère** (`VmManager.SetTemplate`) : la machine va écrire dans cette superposition, et un
  clone fait après qu'elle est redevenue modèle doit partir de ce qu'elle contient (nouvelle couche `Modele 2`). Testé
  contre le vrai qemu-img (`LinkedCloneTests`).
- Une copie ou un clone n'est jamais un modèle (`CopyDescriptor`). Les protections des clones liés s'appliquent telles
  quelles (fichiers du modèle non supprimables tant qu'un clone existe).
- Vérifié dans l'app sur une copie jetable de Debian : deux déploiements sur la même image, clone démarré jusqu'à
  l'invite de connexion, refus de `winqemu start` et `winqemu snapshot create` sur le modèle, suppression refusée puis
  permise une fois les clones supprimés.

## Import (VMware, VirtualBox, Hyper-V, OVF)

`WinQEMU.Core/Import` : un fichier choisi devient un `ImportPlan` (montré, puis confirmé), puis
`VmLibrary.ImportAsync` convertit chaque disque en qcow2 dans un nouveau dossier. **La machine source
n'est que lue** et reste utilisable dans son hyperviseur.

- **VMware** (`VmxReader`) : `.vmx` à plat, clés insensibles à la casse, échappements `|XX`. Les
  deltas d'instantanés (`disk-000001.vmdk`) se lisent par `qemu-img convert` (vérifié sur une vraie
  chaîne vmdk). Refusé : dossier `.lck` présent (machine ouverte), machine chiffrée. Suspendue : l'état
  mémoire est perdu, c'est dit.
- **VirtualBox** (`VboxReader`) : XML, éléments cherchés par nom local n'importe où sous la machine
  (VirtualBox 7 en a déplacé). **Refusé si le disque attaché est différentiel** (instantanés) : le pilote
  VDI de QEMU ne lit que les images de base ; le message dit comment s'en sortir.
- **Hyper-V** : la configuration `.vmcx` est binaire et non documentée, donc **seul le disque**
  (`.vhdx`/`.vhd`) s'importe, avec des valeurs prudentes. Le microprogramme vient de la table de
  partitions (signature GPT `EFI PART` au secteur 1, lue par `qemu-img dd`) : GPT → UEFI, sinon BIOS.
  `.avhdx` (point de contrôle) refusé avec la marche à suivre.
- **Disque seul : le système se déduit des types de partitions** (`VmImporter.DetectFamily` : MSR ou
  récupération Windows → Windows, SATA ; partition Linux, LVM, swap → Linux, **VirtIO**). Pas SATA pour
  un Linux venant de Hyper-V : Ubuntu 26.04 y construit son initramfs avec dracut en mode *hostonly*,
  qui ne contient que `hv_storvsc` ; en SATA l'invité finit en « emergency mode » (`/dev/disk/by-uuid/…
  does not exist`). En VirtIO il démarre, `virtio_blk` étant compilé dans le noyau Ubuntu. Une
  distribution qui en fait un module échoue sur tout contrôleur : la note donne la commande dracut.
- **Même contrôleur que la source quand QEMU l'a** : Windows ne démarre que sur un contrôleur dont il
  charge le pilote au démarrage. D'où `DiskBus.LsiSas` (mptsas1068, le défaut VMware pour Windows),
  `DiskBus.Pvscsi`, `NicModel.Vmxnet3`, NVMe, SATA, IDE. LSI Logic parallèle, BusLogic, synthétique
  Hyper-V : pas d'équivalent → SATA, avec la note `sc config storahci start= boot` pour Windows.
- Adresse MAC de la source **gardée** (l'invité y attache sa configuration réseau). Mode réseau
  toujours NAT (ponté et hôte seul exigent un pilote TAP), c'est dit.
- Pas de NVRAM UEFI reprise (formats propres à chaque hyperviseur) : le microprogramme retombe sur le
  chemin de secours `\EFI\BOOT\BOOTX64.EFI`, présent pour Windows et la plupart des distributions.
- **OVF** (`OvfReader`, `.ova` ou `.ovf` à côté de ses fichiers) : XML du DMTF, éléments et attributs cherchés par
  nom local (OVF 1 et 2 n'ont pas le même espace de noms ; `vmw:osType`, `vbox:OSType`). Matériel en éléments CIM
  (ResourceType 3 CPU, 4 mémoire, 5/6/20 contrôleurs, 17 disque, 10 carte) reliés par InstanceID/Parent ; UEFI par
  `vmw:Config firmware=efi` ou le `Firmware` du `vbox:Machine` embarqué. **Les disques d'un .ova sont lus sur place,
  rien n'est extrait** : `TarArchive` donne la position de chaque fichier, et qemu-img ouvre un nom `json:` (pilote
  vmdk sur la fenêtre `offset`/`size` du pilote raw ; vérifié octet pour octet). Disque compressé (`ovf:compression`)
  ou découpé (`chunkSize`) : refusé.

## Export OVA (`Export.OvaExporter`)

Bouton **Exporter**, machine arrêtée (mêmes conditions que le clonage). Un `.ova` tar : descripteur `.ovf`,
manifeste `.mf` (SHA-256), puis chaque disque en **VMDK streamOptimized** (`qemu-img convert` lit toute la chaîne :
état actuel, sans les instantanés, base d'un clone lié comprise).

- **Descripteur à la manière de VMware**, l'importateur le plus strict (`OvfWriter`) : famille `vmx-14`, SATA en
  `vmware.sata.ahci` (VMware refuse le `AHCI` de VirtualBox), système par `vmw:osType` **et** `vbox:OSType`, UEFI par
  `vmw:Config`, éléments rasd dans l'ordre alphabétique du schéma.
- **VirtualBox ne lit le microprogramme que dans sa propre configuration** (`vbox:Machine`, format 1.16) : sans elle,
  une Debian UEFI était importée avec le BIOS du type Debian et s'arrêtait sur « No bootable medium found! ». Quand
  elle est présente, VirtualBox l'emploie à la place des éléments CIM ; ses disques renvoient à ceux de la DiskSection
  par `vbox:uuid`. Carte VirtIO gardée pour un Linux (VirtualBox l'a), Intel sinon. Vérifié par `VBoxManage import`
  puis `showvminfo` (firmware EFI, SATA, virtio) et en démarrant la Debian exportée dans VirtualBox 7.2.
- **VirtualBox ne sait pas importer un disque entièrement vide** venant de qemu-img (`VERR_EOF` à la création du
  support) ; dès que le disque contient des données, l'import passe.
- **VirtIO n'existe ni chez VMware ni chez VirtualBox** : disques VirtIO → SATA, cartes VirtIO/RTL8139 → e1000e pour
  Windows (plus de pilote e1000 depuis Windows 8), e1000 sinon. Le dialogue dit quoi faire avant d'exporter (pilote
  AHCI de Windows, initramfs dracut *hostonly* d'un Linux), comme les notes de l'import dans l'autre sens.
- **Le manifeste précède les disques** dans le tar (OVF 1.0) : chaque disque est d'abord converti dans
  `<paquet>.ova.parts`, à côté du paquet (même volume : le temporaire de C: pourrait être plein quand la destination
  ne l'est pas), haché, puis recopié et supprimé. Il faut donc environ deux fois la taille du paquet ; le dialogue le dit.
- **Tar écrit à la main** (`TarArchive`) : ustar simple, et au-delà de 8 Gio la taille en base 256 de GNU (lue par
  GNU tar, VirtualBox, 7-Zip, Python), pas un en-tête PAX qu'un lecteur qui ne le connaît pas prendrait pour un fichier
  « PaxHeader/… ». Vérifié avec Python et GNU tar. Annulé ou échoué : paquet et fichiers de travail supprimés.

## Chiffrement des disques (`Qemu.DiskKey`)

Comme le chiffrement de VM de VMware, **disques seulement** (choix de l'utilisateur) : qcow2 LUKS (AES-256 XTS, défaut de
QEMU), un mot de passe pour toutes les images d'une machine (`VmConfig.DisksEncrypted`). Descripteur et NVRAM en clair.

- **Vérifié sur ce build (QEMU 11.1)** : create, info, check, snapshot -c/-a/-d, convert dans les deux sens, commit,
  rebase -u, resize, amend (emplacements de clé), map, measure, qemu-system. `info` lit l'en-tête **sans** mot de passe
  (instantanés compris) ; `snapshot -l`, lui, en veut un. Ouvrir une image coûte 2,3 s (PBKDF, bon ou mauvais mot de
  passe). Compression et chiffrement sont incompatibles (« not supported at the same time »).
- **Jamais de mot de passe en ligne de commande** : objet `secret,file=\\.\pipe\winqemu-<hex>`, tube servi une fois
  (`SecretPipe` : DACL de l'utilisateur, `FirstPipeInstance`, PID du client vérifié). **Fin de fichier = fermer le
  tube** : après `DisconnectNamedPipe`, QEMU lisait « Invalid argument ».
- **Chaque niveau d'une chaîne veut sa clé** (`backing.backing.encrypt.key-secret=…`) et nommer un niveau absent échoue :
  les niveaux sont lus par `qemu-img info -U --backing-chain` (sans mot de passe), pas déduits du descripteur (la base
  d'un clone lié continue chez l'original). `rebase -u` : clé du haut seulement (la base n'est pas ouverte).
- `QemuImgService.WithKey` : toute image chiffrée ouverte avec la clé, toute image créée ou convertie chiffrée (qcow2
  seulement, sinon refus) ; seules exceptions voulues : export VMDK (le dialogue le dit) et déchiffrement.
- **Instantané à chaud** : `blockdev-snapshot-sync` créerait une superposition en clair. À la place : `qemu-img create -u`
  chiffrée, `blockdev-add` avec le secret déjà dans QEMU, `blockdev-snapshot` (vérifié sur un vrai QEMU).
- Mot de passe vérifié à la saisie (`qemu-img map --max-length=512`), gardé en mémoire pour la durée de WinQEMU, ou dans
  le Gestionnaire d'identification (`DiskPasswordStore`, « WinQEMU/<Id> ») si « Mémoriser ». Démarrage automatique :
  seulement avec un mot de passe mémorisé, sinon la machine est laissée et c'est dit.
- **Chiffrer / déchiffrer** = `convert` de chaque chaîne puis échange (instantanés perdus, anciennes images supprimées,
  pas effacées) ; **changer le mot de passe** = `amend` en deux passes (ajout partout, puis retrait de l'ancien).
  Clones liés : même mot de passe des deux côtés, et chiffrer, déchiffrer ou changer le mot de passe y est refusé.

## Périphériques USB de l'hôte

Bouton **USB** de la barre d'outils : connecter / déconnecter à chaud (machine en marche), et
« Connecter à chaque démarrage » (`VmConfig.UsbDevices`, appliqué par la ligne de commande).

- **QEMU n'ouvre que les périphériques sur WinUSB** (pilote, ou filtre inférieur : libusb accepte les
  deux). Sinon libusb répond « unrecognized device driver », QEMU **réessaie toutes les 2 s sans rien
  dire** et l'invité voit un « USB Host Device » vide. D'où `HostUsbEnumerator` (SetupAPI, interface
  `GUID_DEVINTERFACE_USB_DEVICE`) qui lit le pilote de chaque périphérique, et l'aide qui guide vers
  Zadig. Ce QEMU n'utilise pas UsbDk (non installé ici, et pas de chemin vérifié).
- **`attached` (qom-get) est le seul signal fiable** : usb-host reste détaché tant que libusb n'a pas ouvert
  le périphérique. `VmSession.ConnectUsbAsync` le surveille ~3 s puis retire le périphérique qu'il a créé ;
  un périphérique « au démarrage » qui attend n'est jamais retiré (il se connectera une fois branché).
- **Contrôleur `qemu-xhci,id=wq-usb` sur toutes les machines, après tous les autres périphériques PCI** :
  ajouté à une machine existante, il prend le slot suivant au lieu de décaler les autres (une carte réseau
  Linux nommée d'après son slot, `enp0s3`, perdrait sa configuration).
- Identité = vendeur:produit (`wq-usb-0781-559c`), seule correspondance stable que usb-host offre sous
  Windows ; deux périphériques identiques ne se distinguent pas (QEMU prend le premier).

## Dossiers partagés

Comme ceux de VMware (`/mnt/hgfs`) : **WinQEMU sert lui-même les dossiers**, par un canal privé, et
l'invité les trouve dans `~/Partages/<nom>`. Ni réseau, ni compte, ni mot de passe, ni droits
administrateur ; ajout et retrait à chaud. Section **Dossiers partagés** du résumé (`VmConfig.SharedFolders`).

- **Serveur 9P2000.L maison** (`Sharing.NinePServer`), le build Windows de QEMU n'ayant aucun 9p
  (« fsdev support is disabled ») ; virtiofs exige un démon Linux. Racine virtuelle : un dossier par
  dossier partagé, relue à chaque requête (`VmSession.UpdateSharedFolders`, appelé par le shell, seul fil
  autorisé à lire la configuration liée à l'interface). Noms validés (pas de séparateur, de `:` — flux
  alternatifs — ni de `..` qui sortirait du dossier) ; lecture seule → `EROFS`. Testé sur un vrai dossier
  (`NinePServerTests`) et monté dans un vrai Ubuntu 26.04.
- **Transport** : port virtio-serial `org.winqemu.shares.0` (socket QEMU en serveur, la session s'y connecte)
  sur le bus `wq-qga-bus`, déclaré en dernier (slots PCI inchangés), présent sur toutes les VM.
- **Montage par qemu-guest-agent** (`GuestShareMounter`, une fois par processus QEMU : `VmSession` relance
  QEMU au redémarrage de l'invité) : `exec 3<>/dev/virtio-ports/…` puis `mount -t 9p -o trans=fd,rfdno=3,
  wfdno=3` (module `9pnet_fd`). Le montage garde le descripteur. `aname=uid:gid` : le serveur déclare les
  fichiers comme appartenant à l'utilisateur de l'invité (premier compte humain de `/etc/passwd`).
- **Protocole de l'agent sans greeting** : `guest-sync-delimited` d'abord (réponse précédée de 0xFF) ; une
  synchronisation ratée rend la connexion inutilisable, d'où une connexion neuve par essai.
- **Abandonné : le partage Windows (SMB) joint par le NAT à 10.0.2.2.** Windows refusait le compte Entra ID
  de l'utilisateur venant de l'invité (journal NTLM, évènements 4023 `AzureAD\…`), et il fallait UAC, un
  mot de passe stocké et un partage visible du réseau local.
- **Invités Windows : WebDAV** (choix de l'utilisateur, plutôt que WinFsp + un outil invité : rien à installer à part
  l'agent). `Sharing.WebDavServer`, un par exécution de QEMU, démarré seulement pour un invité Windows
  (`VmSession.StartWebDavShares`), sur `127.0.0.1` derrière un premier segment secret ; l'invité le joint par sa carte
  NAT, où `10.0.2.2` est le loopback de ce PC : `\\10.0.2.2@port\secret`. Le client intégré de Windows (WebClient) ;
  corps des PUT écrits en flux (Windows envoie un fichier entier d'un coup, jusqu'à 4 Go) ; PROPPATCH toujours acquitté
  (Windows déclare la copie échouée sinon) ; LOCK acquitté sans verrouiller. Configuré par l'agent en SYSTEM
  (`GuestShareMounter.WindowsSetupScript`) : WebClient automatique, limite de fichier 50 Mo → 4 Go, adresse dans
  `HKLM\SOFTWARE\WinQEMU`, puis une **tâche d'ouverture de session du groupe Users** (SID `S-1-5-32-545`, les noms
  de groupe sont traduits) qui monte le lecteur dans la session de l'utilisateur (`WindowsMapScript` : un `net use`
  fait en SYSTEM ne serait visible que de SYSTEM), de Z: vers le bas, nommé « Partages WinQEMU », 10.0.2.2 en zone
  intranet. Vérifié sur le Windows 11 de l'utilisateur (lecture, écriture, lecteur dans l'Explorateur).
- **L'agent invité n'est pas dans virtio-win-guest-tools** (qui n'apporte que spice-vdagent) : `guest-agent\qemu-ga-x86_64.msi`
  du même lecteur, à installer à part ; le message de WinQEMU le dit pour un invité Windows.

## Outils invités

Comme les VMware Tools (`Services.GuestTools`) : l'agent invité, dont dépendent dossiers partagés, glisser-déposer, taille
d'écran et graphique réseau, plus spice-vdagent (presse-papiers) ; sous Windows, les outils virtio-win (pilotes, agent
spice) et l'agent, installateur à part sur la même ISO. Vérifiés à chaque processus QEMU (`MainViewModel.CheckGuestTools`,
pas pendant une installation ni sur une ISO) : agent attendu 3 min, puis interrogé (Windows : service `spice-agent` ;
Linux : `spice-vdagent`, **sauf invité sans bureau** — aucune session dans `/usr/share/xsessions` ni `wayland-sessions` :
le presse-papiers n'y a pas d'usage, et la Debian serveur de l'utilisateur était dite incomplète à tort ; `systemctl
get-default` y répondait pourtant `graphical.target`). Absents : lien dans la barre de la console et section
**Outils invités** du résumé. L'interface ne cite pas les VMware Tools (choix de l'utilisateur).

- **La conclusion est écrite dans le journal de la machine** (« Guest tools: complete. », « … did not answer within 3 min. »…).
  **Journal de l'interface** : `AppendDiagnostic(message)` écrit dans le journal de la machine affichée (celui de sa
  `VmSession`, gardé), `AppendDiagnostic(config, message)` dans celui d'une machine donnée, pour une tâche de fond
  (démarrage avec WinQEMU, fin d'installation, affichage, instantané automatique…). Avant, tout allait dans le texte
  affiché, refait au changement de sélection : une ligne pour une machine non affichée (WinQEMU caché, démarrage par la
  commande) était perdue, ou montrée un moment sous une autre. Vérifié : Fedora affichée, Ubuntu démarrée par la commande,
  ses lignes dans son journal et aucune dans celui de Fedora. Le bandeau dit « absents ou incomplets » aussi pour un agent qui n'a pas répondu à temps : une fausse alerte
  vue sur Ubuntu venait de scripts d'essai qui interrogeaient l'agent pendant le démarrage (son canal n'a qu'un client) ;
  démarrage propre avec ouverture de session automatique : « complete » 31 s après le démarrage (10/11/2026).

- **Perl fait partie des outils d'un invité Linux** : le récepteur du dépôt, l'émetteur de la récupération et la pompe des
  dossiers partagés sont en Perl, présent sur Debian, Ubuntu (perl-base essentiel) et openSUSE, mais **pas sur une Fedora
  installée avec Xfce** : un glisser hors de l'invité lisait sa liste puis échouait sur « Failed to execute child process
  "perl" ». La vérification le teste avec ses modules (`perl -MIO::Handle -MSocket`, `%!`), le signale (« Perl n'est pas
  installé… », `GuestTools.Report`), « Installer les outils invités » l'installe (`FedoraPerl` : `perl-interpreter perl-IO
  perl-Socket perl-Errno`, Fedora découpant Perl en petits paquets ; `perl-base` ou `perl` ailleurs), et l'installation
  automatique de Fedora l'ajoute à ses paquets. Vérifié sur Fedora 45 Xfce : Perl retiré, journal « Perl is missing »,
  script d'installation par l'agent, vérification « perl ».
- **Agent bridé par la distribution** (`GuestToolsState.Restricted`, Fedora et openSUSE installés à la main) : l'agent
  répondait, dossiers partagés, copies et glisser échouaient, et rien ne disait pourquoi (openSUSE : « could not be
  checked », état inconnu, aucun bouton). Détecté à la vérification : `guest-exec` refusé (« Command guest-exec has been
  disabled », `IsExecDisabled`), ou **écriture refusée dans le dossier d'un utilisateur** (`mktemp` dans le premier
  `/home/*/`, seulement si la commande tourne dans le domaine `virt_qemu_ga_t` : un dossier que root ne peut pas écrire
  pour une autre raison, NFS ou lecture seule, n'est pas un agent bridé, et l'offre reviendrait sans fin). Pas `ls /home` :
  Fedora 45 le permet à l'agent confiné, ainsi que lister le dossier ; lire un fichier y est refusé, et
  `/sys/fs/selinux/enforce` lui est invisible. Rien ne se corrige par un agent bridé : « Installer les outils invités »
  **tape** la même commande que sans agent, qui fait désormais ce que fait l'installation automatique
  (`LinuxReleaseAgent`) : domaine `virt_qemu_ga_t` permissif (effet immédiat, sans redémarrer l'agent : vérifié), agent
  **démarré, pas redémarré** (sous Debian le paquet l'a déjà démarré, et un redémarrage tuerait ce que WinQEMU vient de
  lancer par lui), puis, seulement si la ligne de commande **de l'agent en marche** bloque `guest-exec` (liste de blocage
  qui le nomme, ou liste d'autorisation sans lui, comme la famille Red Hat), filtre vidé dans `/etc/sysconfig/qemu-ga`
  (`FILTER_RPC_ARGS=`, et `BLACKLIST_RPC=`, son ancien nom) et agent redémarré. Ce fichier est celui de l'administrateur,
  lu en dernier par l'unité d'openSUSE comme par celle de Red Hat ; l'unité d'openSUSE ne dit que `${FILTER_RPC_ARGS}`,
  posé dans `/usr/etc/sysconfig/qemu-ga` : chercher `block-rpcs` dans `systemctl cat` ne trouvait rien. **Un agent qui
  répond n'est pas la fin de la commande** (bridé, il répondait avant ; tout juste installé sur openSUSE, il répond deux
  secondes avec `guest-exec` bloqué) : dans les deux cas on redemande jusqu'à ce qu'il ne soit plus bridé (10 min).
  « Signaler l'espace libre (TRIM) » reste offert à un agent bridé (`guest-fstrim` n'est pas `guest-exec`).
  Vérifié dans l'app (12/11/2026) sur openSUSE DE et Fedora test remis dans l'état d'un invité installé à la main (module
  SELinux et surcharge retirés), commande tapée dans une console texte en root : openSUSE détecté « guest-exec is
  blocked », agent libéré (la suite a échoué sur `spice-vdagent`, absent du dépôt DVD de cette machine, sans rapport) ;
  Fedora détecté « SELinux confines the agent », « installed » 12 s après la saisie. Non vérifié : sudo demandant un mot
  de passe dans un terminal de bureau, un invité réellement installé à la main, sans agent du tout, ou de la famille Red
  Hat (liste d'autorisation, de mémoire).
- **Bureau détecté dossier par dossier** : `ls xsessions/*.desktop wayland-sessions/*.desktop` échouait dès qu'un des
  deux dossiers manquait (X11 seul sous Xfce, Wayland seul sous Ubuntu) et l'invité passait pour « sans bureau » : sans
  `spice-vdagent`, il aurait été dit complet à tort.
- **Sans agent, rien ne s'exécute dans l'invité.** Linux : WinQEMU **tape** la commande (`apt-get`/`dnf`/`zypper`/`pacman`)
  dans un terminal que l'utilisateur a ouvert (`Input.KeyboardText` : touches de la disposition de ce PC, AltGr = Alt droit,
  touche morte suivie d'espace, détectée par `ToUnicodeEx` avec les modificateurs ; un caractère à la fois, le tampon PS/2
  de l'invité perdrait des touches). Windows : WinQEMU insère l'ISO virtio-win et l'utilisateur lance
  `virtio-win-guest-tools.exe` **puis** `guest-agent\qemu-ga-x86_64.msi`.
- **Abandonné : taper la commande dans Exécuter (Win+R) de Windows.** Une commande PowerShell tapée là et lancée en
  administrateur est exactement l'attaque « ClickFix » : Microsoft Defender l'a arrêtée
  (`Behavior:Win32/SuspClickFix.G2`, gravité 5) et a tué l'installateur virtio qu'elle avait lancé. Une protection ne se
  contourne pas. Une première version (msiexec de l'agent seul) passait, mais n'aurait servi à rien :
- **Sous Windows l'agent passe par le pilote virtio-serial des outils virtio-win** : désinstaller les outils a fait taire
  un agent qui fonctionnait. Les outils d'abord, donc.
- **Le reste passe par l'agent**, en SYSTEM ou root, sans question : sous Windows, l'éditeur Red Hat est approuvé d'abord
  (signature d'un catalogue de pilote), sinon chaque pilote demande « Installer ce logiciel de périphérique ? ».
- **ISO virtio-win jamais téléchargée** : celle déjà choisie (`AppSettings.VirtioWinIsoPath`), sinon celle qu'une VM de la
  bibliothèque utilise (nom `virtio-win*.iso`), sinon la plus récente des Téléchargements, sinon demandée (où la trouver
  est dit). Insérée dans le lecteur principal, qu'elle remplace (`ChangeIsoAsync`).
- Une fois l'agent là, montage des dossiers et affichage sont relancés (ils avaient pu abandonner faute d'agent).
- Vérifié sur une copie jetable de Windows 11 : agent désinstallé puis revenu ; outils et agent désinstallés,
  diagnostic par captures `screendump` (PNG) et une console tapée par QMP. Saisie testée en AZERTY et QWERTY.

## Taper le presse-papiers

Bouton de la barre de la console et du plein écran : le texte du presse-papiers de ce PC est **tapé** dans l'invité,
pour ce qui n'a pas de presse-papiers partagé (console texte d'une Debian serveur, installation, BIOS, invité sans
outils). Même `Input.KeyboardText` que la commande des outils invités, envoyé par `VmSession.TypeAsync` (un caractère
par appel QMP, 15 ms entre deux). Tout le texte est converti **avant** la première touche : un caractère sans touche est
refusé en le nommant, sans moitié de texte tapée. Confirmation au-delà de 300 caractères.

- **Lettre qu'aucune touche ne porte** (ê, ë, ñ en AZERTY) : décomposée (NFD), touche morte de l'accent puis la lettre ;
  sans touche morte pour cet accent (É en AZERTY), refusée.
- **Positions de touches, pas caractères** : l'invité doit avoir la disposition de ce PC.
- **Invité Linux : un accent derrière AltGr est une touche ordinaire** (`ToKeystrokes(…, linuxGuest: true)`, profil de
  famille Linux, et la commande des outils invités). Windows en fait des touches mortes (français : ~ sur AltGr+2, ` sur
  AltGr+7), suivies d'une espace pour avoir le caractère seul ; les dispositions Linux du même nom donnent le caractère
  aussitôt, et « ~/x » sortait « ~ /x » (Debian « fr latin9 », Fedora « fr »). Sans l'espace pour un invité Linux ; une
  lettre qui aurait besoin de cet accent (ñ en français, qui sortirait « ~n ») est refusée. Les touches mortes sans
  AltGr (^ ¨) le restent partout. **Appliquée à la seule disposition française** (`IsFrench`, 0000040C) : la disposition
  belge de Linux, par exemple, garde ses accents AltGr morts comme Windows. Non vérifiée sous un bureau (xkb « fr » :
  AltGr+2 = `asciitilde`, non morte).
- Vérifié sur la console texte d'une copie jetable de Debian 13 (relu par `/dev/vcsu1` via l'agent) et dans Exécuter
  d'un Windows 11. **Bouton vérifié de bout en bout sur le PC de test** (12/11/2026, invite de connexion d'une console
  texte Fedora, relue par `screendump`) : `été à çà: {a}[b]|c\d @home #1 €5 <ok> "q" 's' ù% êö` tapé juste ; « ~/x »
  sortait « ~ /x », corrigé (ci-dessus) et revérifié : ``cd ~/x_y `date` ~ ^2 été êö`` tapé juste, « ñ » refusé ;
  « École » refusé sans rien taper. Le message de refus finissait par « (Parameter 'text') » (nom de paramètre de l'`ArgumentException`), retiré.

## Capture de la console

Bouton appareil photo de la barre de la console et du plein écran (machine en marche ou en pause) : `screendump` de QMP
au format PNG, **à la résolution de l'invité** quelle que soit la mise à l'échelle (`VmSession.CaptureScreenAsync`),
dans `Images\WinQEMU\<machine> aaaa-MM-jj HH.mm.ss.png`, copiée dans le presse-papiers (image et fichier) ; un menu
volant le dit, avec « Afficher dans l'Explorateur ». QEMU écrit d'abord dans le dossier temporaire puis le fichier est
déplacé : qemu-system ouvre ses chemins par l'API ANSI, et la destination n'existe pas encore, donc n'a pas de nom 8.3.
Vérifié sur un clone de Debian (PNG 1364 × 786). Copie dans le presse-papiers vérifiée sur le PC de test (12/11/2026,
Fedora) : image 1966 × 1524 et fichier (`FileDrop`) du même PNG, menu volant affiché.

## Démarrage automatique et arrière-plan

- **Démarrer avec WinQEMU** (section Démarrage automatique du résumé, `VmConfig.AutoStart` /
  `AutoStartDelaySeconds`) : comme le démarrage automatique de Hyper-V, chaque machine après son délai compté depuis
  l'ouverture de WinQEMU, le plus court d'abord (`Models.AutoStartPlan`). Même chemin que le bouton Démarrer
  (`MainViewModel.StartMachineAsync`). Une machine démarrée ou supprimée entre-temps est laissée.
- **Consoles des machines non affichées** : seule la machine sélectionnée était suivie ; une autre qui démarre (avec
  WinQEMU, ou relancée par sa session au redémarrage de l'invité) aurait laissé sa fenêtre SDL sur le bureau.
  `MainViewModel` suit toutes les sessions (`BackgroundConsoleStarted`) et `VmConsoleHost.AdoptAsync` la gare aussitôt
  (intégrée, puis garée ; `AttachAsync` attend une adoption en cours). Aucune fenêtre QEMU visible vérifié toutes les
  0,5 s. **Piège** : la première console affichée pouvait sortir du garage sans que `WS_CLIPSIBLINGS` soit posé sur le
  pont XAML (seul le chemin « trouver et intégrer » le faisait) : console noire, XAML peint par-dessus.
- **Fermer avec des machines en marche** : « Laisser en arrière-plan » (par défaut), « Arrêter et quitter » (ACPI pour
  toutes, progression, « Éteindre maintenant » pour celles qui ne répondent pas), « Annuler ». En arrière-plan la
  fenêtre est seulement cachée (`AppWindow.Hide`) : les consoles restent ses enfants, rien à retrouver au retour.
  Icône de zone de notification Win32 (`TrayIcon`, WinUI n'en a pas ; remise à « TaskbarCreated »).
- **La fenêtre rouvre où elle a été fermée** (`MainWindow.Placement`, `AppSettings.Window`) : même écran, position, taille,
  agrandie ou non. `GetWindowPlacement` à la fermeture (rectangle restauré, juste même agrandie, réduite ou cachée), à
  l'entrée du plein écran (dont le rectangle est celui de l'écran) ; `SetWindowPlacement` avant le premier affichage,
  **deux fois** (posée sur un écran d'une autre échelle, la fenêtre est redimensionnée par le changement de DPI), puis
  agrandie à l'affichage. Écran disparu : Windows la place. Vérifié entre l'écran 1920 x 1200 et le second de l'utilisateur.
- **Une seule instance** (`Program.Main`, `DISABLE_XAML_GENERATED_MAIN`) : relancé, WinQEMU passe l'activation à
  celui qui tourne (`AppInstance.RedirectActivationToAsync`), qui réaffiche sa fenêtre. Deux instances listeraient les
  mêmes machines sans se voir.
- **Ouverture de session** : tâche de démarrage du paquet (`uap5:StartupTask` `WinQEMUStartup`, désactivée par
  défaut), interrupteur dans les Paramètres (`StartupTask.RequestEnableAsync`) ; désactivée dans Windows
  (`DisabledByUser`), seul Windows peut la réactiver, c'est dit. Lancé ainsi (`ExtendedActivationKind.StartupTask`),
  WinQEMU reste dans la zone de notification ; l'initialisation ne dépend plus du `Loaded` de la fenêtre.
- **Arrêt, redémarrage ou déconnexion de Windows** (`SessionEndWatcher`, comme VMware Workstation) : machines en
  marche, `WM_QUERYENDSESSION` est refusé avec un motif (`ShutdownBlockReasonCreate`, « Arrêt des machines virtuelles :
  Ubuntu… », tenu à jour) qui place WinQEMU sur l'écran « ces applications empêchent l'arrêt » ; les invités reçoivent
  le bouton d'alimentation, et WinQEMU quitte une fois tous arrêtés, ce qui laisse Windows continuer. Annulé
  (`WM_ENDSESSION` faux), WinQEMU reste ; les invités déjà priés s'arrêtent quand même. Vérifié en envoyant ces
  messages à la fenêtre (Ubuntu éteint par lui-même, code 0, puis WinQEMU fermé) ; pas sur un vrai arrêt de Windows.
- **Arrêter un invité Linux passe par l'agent** (`VmSession.RequestShutdownAsync`, donc bouton, commande, dossier, fermeture
  de WinQEMU, arrêt de Windows) : ce qu'un bureau fait du bouton d'alimentation lui appartient. Plasma n'affichait que son
  écran de déconnexion (`winqemu stop --wait` abandonnait après 300 s), Xfce ne s'arrêtait pas en 90 s, GNOME compte 60 s.
  Agent qui répond (3 s au plus) et invité Linux : `guest-shutdown` ; sinon, ou sur refus (openSUSE installé à la main le
  bloque), le bouton. **Windows garde le bouton**, auquel il obéit : l'agent y force la fermeture des programmes
  (`EWX_FORCE`). `guest-shutdown` ne répond que s'il échoue (`GuestAgentClient.ShutdownAsync` : le silence vaut accord) ;
  « Redémarrer » prenait ce silence pour un refus et appuyait en plus sur le bouton. Vérifié par `winqemu stop --wait` sur
  Fedora 45 Xfce et openSUSE KDE : arrêtées en 2 s, journal de l'invité « guest-shutdown called » puis arrêt propre.
- **Attendre les machines vivantes** (`VmManager.LiveSessions`), pas `RunningSessions` : une machine priée de
  s'arrêter passe « Arrêt en cours », que `RunningSessions` exclut. Les premières versions de « Arrêter et quitter »
  la croyaient donc arrêtée aussitôt et coupaient QEMU en quittant (Ubuntu « arrêté » en 3 s). Une machine en pause est
  reprise avant le bouton d'alimentation : figée, elle ne l'entendrait pas.

## Ligne de commande (`winqemu`)

Comme `vmrun` de VMware : `winqemu list | status | start | stop | poweroff | pause | resume | snapshot … | show | capture | help`
(aide complète dans `CliText.Usage`). **La commande pilote WinQEMU, pas QEMU** (choix de l'utilisateur) : une machine
démarrée d'un terminal est celle que montre la fenêtre, avec sa console, et rien ne tourne deux fois.

- **`WinQEMU.Cli`**, programme console autonome copié à côté de `WinQEMU.exe` par la référence de projet du shell (il y
  partage le runtime .NET autonome du shell ; `RuntimeIdentifier` déduit de la plateforme, sinon NETSDK1150 : un exe
  autonome ne référence qu'un exe autonome). **Alias `winqemu.exe`** dans le manifeste, extension de l'application
  WinQEMU avec son propre `Executable="WinQEMU.Cli.exe"`. Un alias console exige `SupportsMultipleInstances` sur
  l'application (sinon 0x80080204) : WinQEMU l'a, et reste unique par `Program.Main` (vérifié : deux lancements, un seul
  processus, la fenêtre de celui en arrière-plan réaffichée). **Plus d'application `Cli` cachée** (`AppListEntry="none"`) :
  Partner Center refuse le paquet (« application sans périphérique de contrôle »).
- **Canal** : tube nommé `WinQEMU.cli.<SID>` (`Core.Cli.CliProtocol` : longueur sur 4 octets puis JSON), réservé à
  l'utilisateur (`Cli.CommandServer`), servi une fois la bibliothèque lue. Chaque requête s'exécute sur le fil de
  l'interface (`MainViewModel.RunCommandAsync`) : mêmes chemins que les boutons (`StartMachineAsync`, instantané
  automatique, dossiers partagés…) ; instantanés par un `VmSnapshotsViewModel` à part, avec les mêmes refus.
- **WinQEMU fermé** : la commande le démarre par `IApplicationActivationManager` avec `--background` (seul moyen de
  passer un argument à une application empaquetée) et attend son tube jusqu'à une minute. L'argument se lit dans
  `AppInstance.GetActivatedEventArgs()` : celui d'`OnLaunched` arrivait vide et la fenêtre s'ouvrait devant le terminal.
- Codes de sortie : 0, 1 échec, 2 commande incorrecte ; 10 en interne quand une machine chiffrée attend son mot de
  passe (demandé sans écho si un humain tape, sinon `--password-stdin`). `list --json` : noms et états stables
  (anglais), pour les scripts.
- `stop --wait` : un bouton d'alimentation pressé pendant que l'invité démarre est ignoré (Debian, 20 s après le
  démarrage) ; la commande le dit au bout du délai sans éteindre (`poweroff` le fait).
- **Machine chiffrée** : jamais la boîte de mot de passe de la fenêtre (WinQEMU caché ne la montrerait à personne) :
  `UnlockForCommandAsync` prend le mot de passe donné, sinon celui mémorisé ou déjà saisi, sinon renvoie le code 10.
  Les instantanés aussi (déverrouillés machine arrêtée). Le `VmSnapshotsViewModel` de la commande reçoit
  `IsMachineOff` de la session : la fenêtre le pose pour le sien, et sans lui tout était refusé.
- Vérifié dans le paquet : WinQEMU démarré en arrière-plan par `winqemu list` (4 s, fenêtre cachée), `start --wait`,
  `stop --wait`, `status`, `show`, erreurs (code 2) ; sur une machine de test chiffrée : refus sans mot de passe,
  mauvais mot de passe, démarrage (seul le nom du tube sur la ligne de commande de QEMU), instantané à chaud
  (superposition `encrypted: yes`), instantané hors ligne, restauration, `qemu-img check` propre.

## Son

Carte `intel-hda` jouée par la sortie **DirectSound** de QEMU (`-audiodev dsound`), son propre vérifié à l'oreille.
SDL, l'autre sortie de ce build, garde une session audio ouverte dès le démarrage, invité muet ou non ; DirectSound
n'en ouvre une qu'au premier son de l'invité. Ce build n'a ni `virtio-snd` ni WASAPI.

- **Micro sur demande** (`VmConfig.EnableMicrophone`) : `hda-output` par défaut, `hda-duplex` (entrée ligne alimentée
  par le micro de ce PC) si coché. Avant, toute VM avec son recevait le micro. QEMU tourne au nom du paquet : capacité
  `microphone` déclarée dans le manifeste.
- **Couper et volume par VM** (bouton **Son**, `VmConfig.AudioMuted` / `AudioVolume`) : QEMU n'a aucune commande de
  son. Chaque processus QEMU a sa **session audio Windows** (celle du mélangeur de volume), réglée par
  `Interop.HostAudio.SetProcessVolume`. Réglage propre à chaque processus (mesuré), **mais** Windows retient le dernier
  posé pour `qemu-system-x86_64.exe` et le donne à la session suivante : une VM démarrée après une VM coupée démarrait
  coupée. D'où `MainViewModel.KeepAudioAsync`, toutes les 500 ms : le réglage de chaque VM en marche est posé sur
  toute nouvelle session de son processus, 100 % non coupé compris.
- **Sortie commune** (Paramètres, `AppSettings.AudioOutputId`) : QEMU n'a pas de choix de périphérique. Windows sait
  envoyer une application vers une sortie (« Volume des applications et préférences de périphériques »), par
  l'interface non documentée `Windows.Media.Internal.AudioPolicyConfig` (celle d'EarTrumpet ; IID différent avant
  Windows 10 21H2), mais **par programme, pas par processus** : un QEMU envoyé vers le casque y entraînait les autres,
  et ceux démarrés ensuite (mesuré). D'où un réglage pour toutes les VM (choix de l'utilisateur), reposé au
  démarrage de chacune, avant son premier son. Vide : sortie par défaut de Windows (la préférence est effacée).

## Glisser-déposer de fichiers vers l'invité

Comme VMware : des fichiers ou dossiers glissés de l'Explorateur sur la console (ou choisis par le bouton
**Envoyer**) sont copiés **sur le Bureau de l'utilisateur de l'invité**, un nom déjà pris devenant `nom (2).ext`.
Hôte vers invité seulement. `Sharing.GuestFileDrop`, état par machine dans `MainViewModel.SendFilesAsync`.

- **Port virtio dédié** `org.winqemu.drop.0` (chardev `wq-drop`, sur `wq-qga-bus` : aucun slot PCI ne bouge),
  plutôt que `guest-file-write` de l'agent : mesuré sur Ubuntu, 33 Mo/s par le port contre 11 Mo/s par l'agent
  (JSON et base64 analysés caractère par caractère). Windows : environ 21 Mo/s.
- **Récepteur lancé par l'agent à chaque dépôt** : Perl sous Linux (perl-base est essentiel), C# compilé une fois
  par version par le .NET Framework de Windows (`%ProgramData%\WinQEMU\WinQemuDrop-<empreinte>.dll`, source sur
  l'entrée standard : dans `-EncodedCommand` elle dépasserait la limite de 32767 caractères). Linux écrit en root
  puis donne tout au premier compte humain, Bureau lu dans `user-dirs.dirs` (`~/Bureau`) ; Windows écrit en
  **se faisant passer pour l'utilisateur de la session console** (`WTSQueryUserToken`), Bureau par
  `SHGetKnownFolderPath` avec son jeton (OneDrive compris), donc fichiers à lui. Pas de session ouverte : refusé.
- **L'hôte se connecte au port avant de lancer le récepteur** : sous Linux, lire un port sans hôte rend aussitôt
  « fin de fichier ».
- **Protocole** : READY + dossier, puis `D` dossier, `F` fichier en morceaux d'1 Mio (longueur 0xFFFFFFFF =
  abandon), `X` abandon, `E` fin ; réponse DONE (noms donnés), FAIL (raison) ou ABORTED. Annulé ou échoué, le
  récepteur supprime tout ce que ce dépôt a créé. L'abandon passe **dans** le flux : sous Windows, un hôte qui
  raccroche sans rien envoyer laisse le récepteur bloqué dans `ReadFile` (vérifié) ; le récepteur suivant arrête
  donc l'ancien (fichier pid, et nom de processus ou heure de début pour ne pas tuer un autre) : un port n'a
  qu'un ouvreur. Un récepteur qui meurt sans répondre est découvert par `guest-exec-status` après 15 s de silence.
- **Agent rendu pendant la copie** : son canal n'accepte qu'un client, que partagent affichage et graphiques.
- **Fenêtre de détection** (`Controls.DropCatcher`, comme celle des outils VMware) : la fenêtre SDL est à QEMU, et
  `RegisterDragDrop` n'accepte qu'une fenêtre du processus. Une cible posée sur la fenêtre principale, parente de
  la console, n'a **jamais été appelée** : OLE interroge la fenêtre sous le pointeur, pas ses parents (vérifié par
  un glisser simulé, `DoDragDrop` + souris réelle). Donc, toutes les 100 ms, si un bouton enfoncé **hors** de la
  console (glisser depuis l'Explorateur) est tenu au-dessus d'elle, une fenêtre enfant à nous, invisible (alpha
  1/255), est posée sur la console avec la cible (`ConsoleDropTarget`) ; retirée au relâchement, à la sortie ou au
  dépôt. Un bouton enfoncé sur la console reste à l'invité. `WS_EX_ACCEPTFILES` est retiré à la fenêtre de SDL :
  QEMU ignore ce qu'elle reçoit. Image et légende de glissement par `IDropTargetHelper` et `DropDescription`.
- **Barre de la console à hauteur fixe** : la progression remplaçant l'aide sur deux lignes changeait la taille de
  la console, donc la résolution de l'invité, au début et à la fin de chaque copie.
- Vérifié sur une copie jetable (`-snapshot`) d'Ubuntu 26.04 et de Windows 11 : contenu identique (SHA-256),
  propriétaire, dates, annulation sans reste, nom numéroté. Le récepteur Windows est aussi testé sur l'hôte, un
  tube nommé tenant lieu de port (`GuestFileDropTests`).
- **Fichier pid illisible** : un `drop.pid` rempli d'octets nuls (invité coupé pendant son écriture) arrêtait tout dépôt
  suivant sous Windows (« Impossible de convertir la valeur … en type System.Int32 »). Lu comme deux nombres ou ignoré.

## Récupérer des fichiers de l'invité

Bouton **Récupérer** (barre d'outils, plein écran) : `Dialogs.GuestFilesDialog` parcourt l'invité par l'agent (départ sur
le Bureau de l'utilisateur, `GuestFilePull.HomeAsync` : `user-dirs.dirs` sous Linux, profil chargé non spécial de
`Win32_UserProfile` sous Windows), sélection multiple, puis dossier de destination (Téléchargements proposé) ; l'Explorateur
s'ouvre sur le résultat. `Sharing.GuestFilePull`, même état par machine que le dépôt (`FileTransfer.Label`).

- **Même port que le dépôt** (`org.winqemu.drop.0`), dans l'autre sens, et même verrou par session
  (`GuestFileDrop.LockFor`) : un port n'a qu'un ouvreur. Même fichier pid (`drop.pid`), et chaque émetteur arrête un
  `winqemu-drop` **ou** `winqemu-pull` resté bloqué.
- **Émetteur lancé par l'agent** : Perl sous Linux (`$0 = 'winqemu-pull'`), C# compilé une fois sous Windows
  (`WinQemuPull-<empreinte>.dll`). Lit en root / SYSTEM : tout fichier de l'invité est lisible.
- **Protocole** invité → hôte : d'abord le **marqueur de la copie** (`WQPULL` + GUID, `NewMarker`, passé à l'émetteur),
  puis `T` total, `D` dossier, `F` nom + taille + date puis morceaux d'1 Mio, `E` fin, `X` erreur. Le récepteur saute ce
  qui précède le marqueur (64 Mio au plus) : des octets d'une copie arrêtée avant, lus comme des enregistrements, donnaient
  « Morceau invalide ». 20 s sans rien reçu : `guest-exec-status` dit si l'émetteur est mort.
- **Ne jamais raccrocher pendant que l'émetteur écrit** (`StopSenderWhileDrainingAsync`) : la connexion fermée au milieu
  d'une écriture de l'invité, QEMU garde cette écriture en attente et ne la reprend pas pour la connexion suivante ;
  l'émetteur ne peut ni la finir ni mourir (tué, il est resté des minutes avec un seul fil), garde le port, et toute copie
  suivante échouait (« le canal de copie est absent ») jusqu'au redémarrage de la machine. Vu après un glisser hors de
  l'invité annulé par Échap ; le bouton Récupérer annulé en avait autant. Annulé ou échoué, l'hôte lit et jette le port
  pendant que l'agent tue l'émetteur (`taskkill /F`, `kill -9`), jusqu'à sa fin. L'émetteur réessaie aussi d'ouvrir le
  port 10 s (un port n'a qu'un ouvreur), sauf s'il n'existe pas.
- **QEMU (Windows) ne voit pas qu'une connexion au port a été fermée par l'hôte** : elle reste son client (`CLOSE_WAIT`), une
  lecture dans l'invité bloque comme si un hôte était là, et la connexion suivante attend derrière elle. **Seule une écriture
  de l'invité** lui fait découvrir la connexion morte et prendre la suivante, et cette écriture est perdue (ou retenue : le
  programme invité reste bloqué dans `write`) ; au-delà de quelques connexions en attente, les suivantes sont **refusées**
  (« refusées jusqu'au redémarrage », vu une fois). Le canal de l'agent se comporte de même (d'où une connexion neuve par
  essai). Mesuré sur Ubuntu avec les programmes de l'invité et un client TCP à la place de WinQEMU. Conséquence, vue sous
  Linux : **chaque glisser hors de l'invité après le premier échouait** au bout de 20 s (« l'envoi s'est arrêté (code 0) ») :
  l'émetteur écrivait aussitôt, et le marqueur de la copie partait vers la connexion de la copie précédente.
  **D'où `GuestFileDrop.LinuxPortHandshake`**, partagé par l'émetteur et le récepteur Linux : l'hôte envoie un « feu vert »
  dès sa connexion (le marqueur de la copie, `WQDROPGO…` pour un dépôt), le programme n'écrit rien des siens avant de l'avoir
  lu (il ne passe que par la connexion que QEMU a prise) et, en attendant, écrit un saut de ligne toutes les 0,5 s, **sans
  bloquer**, chacun débusquant une connexion morte ; un saut de ligne arrivé jusqu'à l'hôte est ignoré (octets avant le
  marqueur, ligne vide avant READY). Le récepteur n'écrit plus rien après que l'hôte a raccroché (son `FAIL` arrivait dans la
  connexion suivante). Vérifié : 8 copies et dépôts mêlés à la suite, en partant d'un port bloqué. Les programmes Windows
  n'ont pas changé : mesuré sur Windows 11 (11/11/2026), l'ouverture du port par le programme invité suffit à faire lâcher
  la connexion morte à QEMU, et rien n'est perdu.
- **Port qui refuse toute connexion** (`GuestFileDrop.ConnectToPortAsync`, commun au dépôt et à la récupération) : QEMU
  garde une connexion morte, une seule attend derrière, la troisième est refusée ; or chaque copie se connecte avant de
  lancer son programme invité, donc rien ne débloquait plus le port jusqu'au redémarrage de la machine. Deux copies finies
  avant que leur programme ait ouvert le port suffisent (Perl absent, glisser relâché dans le vide après un Échap).
  Refusée, la connexion fait **ouvrir et fermer le port dans l'invité par l'agent** (`ClearPortCommand` : `type nul >` le
  port sous Windows, `: <>` sous Linux, sans rien écrire), ce qui fait lâcher ses connexions mortes à QEMU, puis réessaie
  3 s avant de dire « démarrée sans le canal de copie ». Reproduit avec un client TCP sur Windows 11 et Fedora ; vérifié
  dans l'app sur Fedora (port mis en refus, glisser de 60 Mo réussi, ligne « Copy port: connection refused… » au journal).
- **Noms** : un nom de premier niveau déjà pris est numéroté ; les caractères interdits par Windows (`:`, `?`…) et
  points ou espaces finaux sont remplacés (`WindowsName`). Lien symbolique vers un fichier : son contenu est copié ;
  vers un dossier : ignoré (boucles). Annulé ou échoué : ce que la copie a créé est supprimé.
- Vérifié sur des copies jetables : Ubuntu (`/usr/share/doc/bash`, empreintes identiques) et Windows 11 (5/5) ;
  dialogue ouvert dans l'app sur Debian. Émetteur Windows testé sur l'hôte avec un tube nommé (`GuestFilePullTests`).

## Glisser des fichiers de l'invité vers ce PC

Comme VMware, invités Windows et Linux avec un bureau (voir plus bas pour Linux) : des fichiers glissés hors
de la console sont déposés sur ce PC (Explorateur ou toute cible qui prend des fichiers). `Sharing.GuestDragOut` (côté
invité), `MainWindow.DragOut` et `Controls.GuestDragSource` (côté PC), `VmConsoleHost.GuestDragLeft`.

- **Un glisser dans l'invité appartient à son Explorateur** ; QEMU ne transporte que la souris. Ce qui est glissé ne se
  sait que dans l'invité, par une fenêtre que le glisser survole : un petit programme (`WinQemuDragOut-<empreinte>.exe`,
  WinForms, compilé par l'agent avec le .NET Framework de Windows, tâche `WinQEMU-DragOut` du groupe Users à l'ouverture
  de session) pose, tant qu'un bouton est tenu et que le pointeur est **sur le bord de l'écran**, une fenêtre presque
  invisible (16 × 16, opacité 0,01, `AllowDrop`) sous le pointeur ; au `DragEnter` il écrit la liste dans
  `C:\ProgramData\WinQEMU\dragout.txt` (droits des Users par `icacls`), lue par l'agent (`guest-file-read`, aucun
  processus lancé). Le dépôt sur cette fenêtre ne fait rien : les fichiers restent.
- **La fenêtre suit le pointeur le long du bord**, et un `SetCursorPos` sur place fait regarder OLE sous le pointeur :
  entre la sortie de la console et la reprise du glisser par ce PC (≈ 1,5 s), le pointeur de l'invité suit celui du PC,
  ramené au bord ; laissée là où elle était apparue, l'invité finissait son glisser sur son bureau, **qui y déplaçait
  l'icône**, et les glissers suivants appuyaient sur du vide.
- **Côté PC** : un bouton appuyé dans la console et tenu hors d'elle (`WatchForDrag`, 100 ms) lève `GuestDragLeft` ; le
  pointeur de l'invité est épinglé au bord (`PinGuestPointerToEdgeAsync`, plus un pixel et retour : OLE agit sur les
  mouvements), la liste est attendue 1,5 s, la copie commence aussitôt dans `TempState\DragOut\<guid>` du paquet (un vrai
  chemin que l'Explorateur sait lire ; un chemin sous AppData serait la vue virtualisée du paquet ; dossiers de plus
  d'une heure supprimés), puis `SHDoDragDrop` avec un `IDataObject` maison (`CF_HDROP`, et ce que l'Explorateur y range
  pour l'image du glisser).
- **Déposé tout de suite, là où on relâche ; c'est la cible qui attend la copie.** Une première version faisait attendre
  le relâchement (`QueryContinueDrag` répondant « continuer » jusqu'à la fin de la copie, comme 7-Zip) : OLE suivait alors
  la souris, et **les fichiers atterrissaient là où était le pointeur à la fin de la copie** (signalé par l'utilisateur).
  Désormais, une fois le bouton relâché, l'objet de données ne donne `CF_HDROP` qu'une fois la copie finie, et une copie
  échouée rend une erreur à la cible. Cette attente ne doit pas bloquer l'interface : l'objet **refuse `IMarshal`**
  (`ICustomQueryInterface`, sinon un CCW .NET est agile) et OLE en reçoit un **mandataire marshalé depuis le MTA**
  (`CoMarshalInterThreadInterfaceInStream` sur un fil du pool) ; tous ses appels, ceux d'OLE comme ceux de l'Explorateur
  depuis son processus, arrivent sur des fils de COM. Avec `IDataObjectAsyncCapability`, l'Explorateur copie sur son
  propre fil ; il demande pourtant les chemins **pendant** son dépôt, avant de passer en asynchrone : `SHDoDragDrop` ne
  rend la main qu'à la fin de la copie depuis l'invité (9 s pour 3 Go ici), souris libre, interface vivante. **OLE ne
  redemande qu'à un mouvement de souris ou une touche** : un `WM_MOUSEMOVE` sur place est posté toutes les 100 ms à la
  fenêtre qui tient la souris (sans lui, un relâchement venu avant le début du glisser attendait 36 s ; un message vide à
  la fenêtre principale ne le réveillait pas). Relâché où rien ne prend de fichiers (effet 0) : fini aussitôt, copie
  abandonnée. L'effet est lu sans `DROPEFFECT_SCROLL` (0x80000000, ajouté par l'Explorateur au-dessus d'une liste).
- **Pas de progression dans la barre de la console pour un glisser** (`FileTransfer.Shown`, demande de l'utilisateur) : la
  cible montre sa propre copie, et la barre « Copie depuis l'invité… » n'avançait pas et son Annuler ne répondait pas (le fil
  de l'interface est tenu par le glisser jusqu'à la fin de la copie). La copie compte toujours comme en cours (un seul
  transfert par machine, un port n'a qu'un ouvreur) ; Échap annule pendant le glisser ; le résultat s'affiche ensuite.
- **Relâchement rendu à l'invité dès le relâchement du PC** (rappel de `QueryContinueDrag`, pas à la fin du dépôt, qui peut
  durer toute la copie : un pointeur ramené sur la console entre-temps emportait le glisser de l'invité, bouton encore tenu,
  qui déplaçait l'icône). Par QMP (le glisser du PC a pris la souris), **et à SDL** quand c'est lui qui a eu l'appui
  (couche du pointeur cachée, `_sdlHasPress`, une fois par appui) : SDL avait vu l'appui, pas le relâchement, et croyait le
  bouton tenu ; il ignorait l'appui suivant et, image au pixel, **tous les glissers suivants ne prenaient rien**, le
  pointeur de l'invité resté au bord (corrigé à la main en postant `WM_LBUTTONUP`, puis dans le code). SDL relâche là où il
  a vu le pointeur en dernier, et QEMU envoie cette position avec le relâchement, d'un coup : si le pointeur de l'invité
  était ailleurs sur le bord (épinglé par QMP, dont la file attend le fil de l'interface), la fenêtre de l'invité n'avait
  pas le temps de suivre et l'icône était déplacée. D'où un `WM_MOUSEMOVE` posté au point du bord, puis le relâchement
  150 ms plus tard, par un minuteur hors interface. Pas de message à SDL qui ne l'attend pas : un mouvement marqué « tenu »
  lui ferait croire à un appui, un clic sur le bord de l'invité.
- **Bouton droit : l'Explorateur répond avant la réponse à son menu** (« Copier ici », « Annuler »), sans effet ; on se
  fie à ce que la cible acceptait au survol, et les fichiers restent (le dossier temporaire est vidé au bout d'une heure).
  **Vérifié à la main par l'utilisateur** : le menu copie, « Annuler » annule. Choisi par un clic simulé ou une touche
  Entrée envoyés depuis un autre processus, « Copier ici » ne copiait rien : sans doute l'essai, pas WinQEMU. Une détection au
  bouton droit a échoué une fois (sur deux) pendant ces essais automatiques, sans explication.
- Vérifié sur une copie du Windows 11 de l'utilisateur (Ryzen, 09/11/2026), glisser simulé (`SetCursorPos`,
  `mouse_event`, processus *per-monitor aware*), empreintes SHA-256 comparées par l'agent : 3 Mo, 400 Mo, 3 Go relâché
  pendant la copie, sur les deux chemins (image à l'échelle : `PointerOverlay` ; image au pixel : SDL) ; **relâché sur
  l'Explorateur pendant la copie puis pointeur ramené sur la console : fichier au bon endroit, icône de l'invité en place**,
  sur les deux chemins ; Échap (copie annulée, dossier supprimé, rien dans la destination, aucun émetteur resté dans
  l'invité) ; relâché hors cible ; glissers de suite après un Échap ; icônes restées en place. Dépôt sur un élément du
  volet de navigation de l'Explorateur : correct.

- **Invité Linux** (`GuestDragOut.LinuxHelperSource`, `LinuxSetupScript`) : sous Wayland aucun programme ne voit un glisser
  qui ne le survole pas, mais GNOME et KDE font passer un glisser de fichiers vers une fenêtre **XWayland** en XDND. D'où
  un petit programme Python (ctypes sur `libX11.so.6`, rien à installer : python3 est là sur les distributions avec
  bureau) : fenêtre X11 *override-redirect* **InputOutput** (une InputOnly n'a pas de surface Wayland), presque
  transparente, `XdndAware` 5 ; au `XdndPosition` il répond `XdndStatus` (copie) et demande `text/uri-list`, écrit la
  liste dans `/var/lib/winqemu/dragout.txt` (même format que Windows), et au dépôt répond `XdndFinished` sans rien faire.
  **Bande de 4 px le long d'un bord entier**, posée seulement quand l'hôte l'écrit dans `/var/lib/winqemu/dragout-edge`
  (`left`… ou `none`, par `guest-file-write` ; `VmConsoleHost.EdgeOfPointer` : le côté de la console que le pointeur a
  quitté), retirée à la fin du glisser (`HideGuestDragEdgeAsync`) : posée en permanence elle prendrait les clics du bord
  (barre de GNOME). Installé par l'agent en root (`/usr/local/lib/winqemu/`), lancé à chaque ouverture de session (autostart
  XDG) et dans les sessions déjà ouvertes : `DISPLAY` et `XAUTHORITY` lus par `systemctl --user show-environment` de
  l'utilisateur, `runuser … setsid -f`. Pièges : sorties du programme lancé redirigées (sinon `guest-exec` ne finit
  jamais, le fils tient les tubes) ; `pkill -f '^[^ ]*python3 …[.]py$'` (sans ancre, il tuait le `sh -c` du script, qui nomme
  le programme) ; **`umask 022` et `chmod` explicites** : l'agent d'openSUSE tourne avec `UMask=0077`, et le dossier du
  programme et l'entrée d'autostart sortaient lisibles de root seul (« Permission denied » dans la session KDE ; Ubuntu n'a
  pas cette restriction). Tout script lancé par l'agent qui crée un fichier destiné à l'utilisateur doit s'en méfier.
  **Aucune session n'est attendue** : l'agent répond bien avant l'ouverture de session, que l'autostart
  couvre ; une première version attendait une session, abandonnait au bout de 5 min, et une ouverture plus tardive ne
  prenait jamais de glisser. Pour la même raison la liste (`dragout.txt`) est créée par l'installation, root, **mode 666** :
  le programme tourne au nom de qui ouvre la session, et la liste ne nomme que des fichiers que l'hôte copie pour son
  propre utilisateur (qui lit déjà tout l'invité par l'agent) ; un utilisateur de l'invité qui l'écrirait ne recevrait rien.
  **Vérifié dans l'app sur une copie d'Ubuntu 26.04 (GNOME, Wayland), 09/11/2026**, glisser simulé vers l'Explorateur :
  fichier de 60 Mo et dossier avec sous-dossier et nom accentué (SHA-256 identiques), dix glissers de suite dont un dépôt
  dans l'invité entre deux, Échap (rien dans la destination, dossier de travail supprimé, aucun émetteur resté), relâché
  hors cible (effet 0), icônes restées en place, session ouverte après l'installation (ouverture automatique). **Une fois
  sur dix, GNOME n'a pas passé le glisser à la bande** : « the guest dragged no files » au journal, rien de cassé, le
  suivant passe. D'où, pour un invité Linux, **le pointeur de l'invité rebougé sur le bord chaque seconde** tant que la
  liste n'arrive pas (quatre fois au plus, 4,5 s d'attente au lieu de 2,5 s ; un glisser rattrapé le dit au journal). Non
  démontré : 60 glissers scriptés de suite sur Ubuntu (11/11/2026), 60 pris du premier coup, aucun à rattraper ; le cas
  d'origine était une série avec un dépôt dans l'invité au milieu. Prix : un appui qui sort de la console sans glisser de
  fichiers attend 2 s de plus avant d'abandonner. openSUSE ou Fedora installés à la main : agent bloqué (`guest-exec`, SELinux), donc rien ; installés par
  WinQEMU, ils ont ce qu'il faut.
  **KDE (KWin) ne passe un glisser Wayland qu'aux fenêtres X11 qu'il gère** : la fenêtre override-redirect, qui suffit à
  GNOME, n'a jamais reçu de XdndEnter (sondé avec une fenêtre rouge visible : override-redirect rien, fenêtre normale la
  liste). Sous KWin (reconnu par `_NET_SUPPORTING_WM_CHECK` → `_NET_WM_NAME`), la bande est une fenêtre gérée de type
  **`_NET_WM_WINDOW_TYPE_DOCK`** (`ABOVE`, hors barre des tâches), **avec sa position et sa taille dans `WM_NORMAL_HINTS`**
  (`USPosition`… min = max) : sans elles, KWin la plaçait au milieu de l'écran et le glisser finissait sur le bureau, qui
  y **déplaçait l'icône**. Gestionnaire d'erreurs X neutre (une fenêtre disparue arrêterait le programme).
  **Vérifié dans l'app sur openSUSE Tumbleweed (Plasma 6, Wayland) installé par WinQEMU, 09/11/2026** : fichier de 60 Mo et
  dossier (SHA-256 identiques), Échap, relâché hors cible, icônes en place ; Ubuntu revérifié ensuite.
  **X11 vérifié aussi** (Fedora 45, Xfce, LightDM, 11/11/2026), image mise à l'échelle (l'invité Xfce ne suit pas la
  fenêtre) : fichier de 60 Mo et dossier, SHA-256 identiques, icônes en place.

## Réseaux virtuels

Comme l'éditeur de réseaux de VMware et les commutateurs d'Hyper-V : des réseaux nommés
(`AppSettings.VirtualNetworks`, pas dans les VM : un réseau leur survit), sur lesquels une carte se branche
en mode `NetworkMode.Network` (`NetworkAdapterConfig.NetworkId`, par id : renommer ne débranche rien).
Bouton à côté des Paramètres (`VirtualNetworksDialog`).

- **Commutateur WinQEMU** (`Networking.VirtualSwitch`) : un par réseau, démarré quand une VM branchée
  démarre, gardé tant que l'app tourne (`VmManager.Networks`). Chaque carte est une netdev QEMU
  `stream` cliente en TCP local (`reconnect-ms=1000` : elle revient seule si le commutateur redémarre).
  Trames préfixées de leur longueur sur 4 octets big-endian. Commutateur à apprentissage : diffusion,
  multicast et inconnus vers tous les autres ports.
- **Adresses réglables** dans l'éditeur, comme chez VMware : adresse du sous-réseau, masque (/8 à /29), plage DHCP en
  adresses complètes (vide = défaut : .100–.199 si le sous-réseau le permet, sinon tout l'intervalle). Ce PC prend la
  première adresse utilisable (.1 en /24), le serveur DHCP la dernière (.254). `DhcpScope` fait les calculs en entiers
  (masque quelconque) ; `VirtualNetwork.Problem` explique une saisie impossible (adresse qui n'est pas un début de
  sous-réseau, plage hors limites…), et le commutateur refuse de démarrer tant qu'elle l'est. Pris en compte au prochain
  démarrage du commutateur. Ni routeur ni DNS annoncés (la route par défaut reste celle de la carte NAT) ; même adresse
  pour une même MAC (hachage).
- **Une seule liste « Connexion » par carte** (Matériel et assistant) : NAT puis chaque réseau par son nom, comme
  VMware liste NAT et ses VMnets (`NetworkChoice`, valeur `NetworkAdapterConfig.Connection` : `nat`, `none`,
  `net:<id>`). Le mode « Ponté » (backend `tap` brut) n'y figure plus : le réseau Externe le remplace. Une
  valeur nulle est ignorée (une liste qui remplace ses éléments la pose un instant).
- **Câble branché / débranché** (`NetworkAdapterConfig.Connected`, comme « Connected » chez VMware) : la carte
  reste dans l'invité mais sans lien, par QMP `set_link` (nommé d'après la netdev, QEMU reporte l'état sur la
  carte). Aucune option de ligne de commande ne démarre une carte lien coupé : posé juste après la connexion
  QMP, avant que le système de l'invité démarre. Seul réglage de la section Réseau modifiable machine en marche.
- **Sous-réseaux incompatibles** (`VirtualNetwork.ConflictWith`, `HostSubnet`) : un réseau interne met ce PC sur
  son sous-réseau, donc refusé s'il chevauche un réseau d'une carte de ce PC (dont le Wi-Fi d'un réseau
  externe). Deux réseaux virtuels quels qu'ils soient ne partagent jamais d'adresses (VMware refuse deux VMnets
  sur un même sous-réseau) ; aucun ne doit chevaucher le NAT de QEMU (10.0.2.0/24). Vérifié à la
  fermeture du dialogue **et** au démarrage du commutateur (le PC a pu rejoindre un autre Wi-Fi depuis).
- **Remplace le mode « Interne » par multicast** (`socket,mcast=`), qui envoyait aussi le trafic des VM
  par la carte de l'hôte à quiconque rejoignait le groupe. Les cartes qui l'utilisaient sont rebranchées au
  chargement sur un réseau privé « Segment N » (`VmManager.MoveMulticastSegmentsToNetworks`).
- **Interne** (les VM et ce PC) : une carte TAP-Windows (`tap0901`) par réseau interne. **Le pilote est
  embarqué** (décision de l'utilisateur, par exception à « jamais embarquer » qui reste la règle pour
  QEMU) : `Assets\Drivers\tap-windows-setup.exe`, l'installateur 9.24.7 d'OpenVPN (GPLv2, source et
  empreinte dans le README voisin), renommé car les points du nom d'origine déclenchaient l'avertissement
  PRI249. `TapDriver` le lance en `/S`, ou ajoute une carte avec son `tapinstall.exe`, en `runas` (une invite
  UAC), après avoir vérifié la signature (WinVerifyTrust + signataire « OpenVPN Inc. ») : un fichier
  substitué ne doit jamais être élevé. Les releases GitHub de tap-windows6 ne publient plus que des `.msm`. WinQEMU l'ouvre (`\\.\Global\{GUID}.tap`, `TapDevice`), la
  « branche » (IOCTL media status, sinon Windows ne demande pas d'adresse) et en fait un port du
  commutateur. Le DHCP réserve `.1` à sa MAC : Windows prend son adresse tout seul, sans droits.
  Accès refusé (5) → réglage « Non-Admin Access » de la carte. OpenVPN Connect (`tap_ovpnconnect`) exclu.
  **Wintun écarté** (étudié le 12/11/2026, non essayé) : maintenu, plus rapide, une seule DLL redistribuable, mais niveau
  IP et non Ethernet (ARP et en-têtes à simuler pour ce PC), pas de DHCP pour ce PC (adresse à poser en administrateur),
  carte ouvrable seulement avec des droits élevés (UAC à chaque démarrage du réseau, ou un service), et la règle 10.2.4
  du Store s'applique pareil. À reconsidérer seulement si TAP-Windows cessait de s'installer sur un Windows futur.
- **Externe** (les VM sur le réseau physique, chacune avec son adresse donnée par la box ; pas de DHCP WinQEMU) :
  la carte physique choisie (`PhysicalAdapter`, `VirtualNetwork.PhysicalAdapterId`) devient un port du
  commutateur par **Npcap** (`Networking.Npcap`, P/Invoke vers `System32\Npcap\wpcap.dll`), comme le pilote de
  pont de VMware, **sans rien régler dans Windows** (l'utilisateur a refusé le pont manuel du panneau de
  configuration). Npcap est installé par l'utilisateur (licence interdisant de l'embarquer), guidé par le
  dialogue ; sans l'option « Restrict … to Administrators only », sinon accès refusé. La carte est ouverte une
  fois à la création du réseau pour le dire tout de suite.
  - **Traduction d'adresses MAC** (`MacTranslator`, testée sans Npcap) : une borne Wi-Fi n'accepte que la MAC du
    poste associé. Les trames des VM sortent sous la MAC du PC (Ethernet et ARP), leur IP est apprise ; ce qui
    revient à la MAC du PC pour l'IP d'une VM lui est réadressé. DHCP : drapeau broadcast posé (somme UDP mise
    à 0), `chaddr` gardé pour que la box donne une adresse par VM. Fait aussi sur Ethernet : pas de mode
    promiscuous, un seul chemin.
  - **IPv6 bloqué dans les deux sens** (le NDP porte des MAC dans de l'ICMPv6 sommé) : sans cela un invité ayant
    reçu des annonces de routeur essaierait l'IPv6 d'abord et attendrait à chaque connexion.
  - **Le PC et ses VM ne se joignent pas par ce réseau** (Windows ne reçoit pas ce qui est injecté sur sa propre
    carte ; ses trames ne se distinguent de celles des VM que par la MAC) : carte NAT ou réseau interne pour ça.
  - **Écartés** : pont Windows d'une carte TAP (manuel), NDISUIO (`\\.\Ndisuio`, réservé à 802.1X :
    administrateur exigé, `SET_ETHER_TYPE` → erreur 87, toute écriture → paramètre incorrect), pilote maison
    (certificat EV et signature d'attestation Microsoft).

## Accès depuis le réseau (à la place d'un vrai pont)

Section **Accès depuis le réseau** (`LanAccess`) : la VM reste en NAT, et des services (SSH, Bureau à
distance, HTTP, HTTPS, autre port) s'ouvrent en un clic par des redirections écoutant sur `0.0.0.0`.
L'app affiche quoi taper depuis une autre machine (`ssh -p 10022 utilisateur@192.168.1.170`).

- **Pourquoi pas de pont** : le backend `tap` de ce QEMU n'accepte que les adaptateurs TAP-Windows
  `tap0901` ; celui d'OpenVPN Connect (`tap_ovpnconnect`) donne « Device 'tap' could not be initialized ».
  Ponter un TAP et le Wi-Fi exige un pont Windows (admin), que bornes d'entreprise et clients VPN
  mettent en échec. Choix de l'utilisateur : l'accès par redirections.
- **À chaud** : `hostfwd_add` / `hostfwd_remove` par le moniteur humain (QMP n'a pas d'équivalent ; succès =
  réponse vide pour l'ajout, « … removed » pour le retrait).
- **Port hôte** : celui de l'invité s'il est ≥ 1024 et libre, sinon +10000, sinon dès 20000 ; les ports déjà
  redirigés par d'autres VM, même arrêtées, sont exclus.
- **Pare-feu** : sans règle entrante pour `qemu-system-*.exe`, rien n'arrive du réseau (l'invite de Windows
  passe inaperçue ou est refusée à un utilisateur standard). Lu par l'API COM `HNetCfg.FwPolicy2` sans
  droits ; règle ajoutée par `netsh` en `runas`, profils privé et domaine seulement (pas public).

## Graphiques de ressources

Panneau « Ressources » du résumé : processeur, mémoire, disque, réseau sur deux minutes (`VmMetricsHistory`, une
mesure par seconde **pour chaque VM en marche**, si bien que les courbes sont pleines quand on en sélectionne une),
dessinés par `Controls.MetricChart` selon le guide de data-viz : une mesure par graphique (jamais deux échelles),
au plus deux séries de même unité, palette de référence (bleu puis orange, pas propre à chaque thème), lignes 2 px,
grille en filet, réticule et valeurs au survol.

- **Processeur** : temps CPU de QEMU rapporté aux processeurs **de la VM** (`VmMetrics.GuestCpuPercent`), plafonné
  à 100 (les fils de QEMU comptent aussi). La part du PC reste affichée sous les graphiques.
- **Disque** : débits tirés des compteurs cumulés de `query-blockstats` ; un compteur qui recule (QEMU relancé
  au redémarrage de l'invité) donne 0, pas un pic négatif.
- **Réseau** : QEMU ne compte rien pour son NAT ; c'est **l'agent invité** qui le dit
  (`guest-network-get-interfaces`, statistiques, loopback exclue), toutes les 3 s, par une connexion ouverte et
  fermée aussitôt : le canal de l'agent n'accepte qu'un client, que partagent dossiers partagés et affichage. Sans
  agent, le graphique le dit.

## Pièges de plateforme (WHPX, QEMU 11)

Chacun a coûté du temps ; les commentaires du code donnent le détail.

- **Modèles CPU `max` / `host` : l'invité n'exécute rien**, pas même le firmware (« Display output is not
  active »). Refusés par `Validate` ; modèle par défaut `qemu64` + extensions.
- **CET / shadow stacks.** Sous WHPX, la feuille CPUID 7 vient de l'hôte et laisse passer les bits CET
  quel que soit `-cpu` (`-cet-ss` est ignoré). Sans XSAVES ni composants CET déclarés, un Linux récent
  active `user_shstk` sans sauvegarder le pointeur de shadow stack : des processus sans rapport meurent à
  la même adresse (`error 44`) puis `systemd` → « Attempted to kill init! exitcode=0x0000000b ».
  `QemuArgumentBuilder.WithConsistentCetState` ajoute `+xsave,+xsavec,+xsaves,+cet-ss,+cet-ibt` (l'invité
  passe de xstate `0x3` à `0x1803`).
- **Pas de reset à chaud avec un firmware UEFI** : « WHPX: Unexpected VP exit code 4 », QEMU fige la VM
  et le shell l'affiche « En pause ». Contourné par `-action reboot=shutdown` ; `VmSession` relance QEMU à
  froid sur un `SHUTDOWN` de raison `guest-reset` ou `host-qmp-system-reset`.
- **Pas d'instantanés mémoire** (`savevm` exige la migration) et **pas de Secure Boot** (SMM refusé par
  WHPX) : les invités Windows 11 n'ont ni TPM ni Secure Boot et demandent le contournement du contrôle
  matériel à l'installation. **Fait par WinQEMU** : une machine `windows-11` qui démarre sur son ISO reçoit
  une clé USB en lecture seule (vvfat sur `<dossier VM>\winqemu-setup`, `usb-storage` sur `wq-usb`) portant un
  `autounattend.xml` (`WindowsSetupAnswer`) qui ne fait que poser les clés `LabConfig` (TPM, Secure Boot, RAM,
  CPU, stockage) dans la passe windowsPE ; l'installation pose ses questions habituelles. Le modèle CPU par
  défaut a déjà SSE4.2 et POPCNT, exigés sans contournement possible par Windows 11 24H2.
- **Installation automatique de Windows** (« installation simplifiée » de VMware, case de l'assistant pour un Windows
  10/11 en UEFI qui démarre sur son ISO) : `WindowsUnattendedSetup` écrit sur la même clé un `autounattend.xml`
  complet (édition Pro par la clé générique publiée de Microsoft, qui n'active rien ; tout le disque ; langue lue
  **dans l'ISO**, `sources\lang.ini` par `IsoFileReader` — les ISO Microsoft sont **UDF seul**, leur arbre ISO 9660
  ne contient qu'un README ; locale, clavier et fuseau de ce PC) et deux scripts. **Toute commande RunSynchronous est
  limitée à 259 caractères** : le script du compte, en ligne (1694), faisait échouer la passe specialize avec
  « L'ordinateur a redémarré de manière inattendue » (lu par Maj+F10 dans `Panther\setuperr.log`) ; d'où
  `WQUSER.PS1` (compte créé puis ajouté aux administrateurs **par SID** S-1-5-32-544, le nom du groupe étant
  traduit) et `WQSETUP.PS1` à la première ouverture de session (certificat de Red Hat approuvé, sinon chaque pilote
  demande confirmation ; virtio-win-guest-tools puis l'agent invité, en silence), lancés depuis la clé quelle que
  soit sa lettre. `VmConfig.UnattendedSetup` : `Pending` → au premier démarrage le shell appuie sur une touche pour
  « Press any key to boot from CD » (une seule fois, sinon l'installation recommencerait à chaque redémarrage) →
  `Installing` → quand l'agent répond, fichiers de réponses supprimés (mot de passe en base64 seulement) et
  `BootFromIso` désactivé.
- **Installation automatique d'Ubuntu** (`UbuntuUnattendedSetup`, même case de l'assistant, profil Ubuntu et ISO qui a
  `casper/vmlinuz`) : fichier `autoinstall` (cloud-init) sur une clé vvfat nommée **CIDATA**, où regarde la source
  NoCloud ; mot de passe en **SHA-512 crypt** (`Sha512Crypt`, vérifié sur les vecteurs de la spécification) ;
  `qemu-guest-agent` et `spice-vdagent` installés d'office. L'installateur demande confirmation sauf si `autoinstall`
  est sur la ligne du noyau : au lieu de réécrire le menu de l'ISO, le **premier démarrage** (`Pending`) lance le
  noyau et l'initrd de l'ISO **directement** (`-kernel`, lus dans l'ISO à la création), les suivants partent du disque
  (`BootFromIso` faux). Avec `-kernel`, l'index de démarrage 0 est pris : les périphériques partent de 1 (sinon
  « The bootindex 0 has already been used »). Vérifié de bout en bout sur l'ISO Ubuntu 26.04 bureau : 24 min, agent
  qui répond, écran de connexion.
- **Installation automatique de Debian, Fedora et openSUSE** (`LinuxUnattendedSetup`, même principe qu'Ubuntu : premier
  démarrage sur le noyau et l'initrd de l'ISO par `-kernel`, ligne du noyau gardée dans `winqemu-setup\cmdline`, puis le
  disque). Fichiers de réponses supprimés quand l'agent répond, comme pour Ubuntu (ils portent l'empreinte du mot de passe).
  **Environnement de bureau au choix** dans l'assistant (demande de l'utilisateur ; `LinuxDesktop`, liste
  `DesktopChoice`) : GNOME, KDE Plasma, Xfce, Cinnamon, MATE, LXQt, ou aucun (console avec SSH), ceux que les trois
  distributions ont toutes ; celui de la distribution par défaut (KDE pour openSUSE, GNOME sinon), repris à chaque
  changement de système. Nommés comme chaque installateur les connaît : tâche tasksel (`xfce-desktop`), environnement
  comps (`@^kde-desktop-environment`, GNOME = `workstation-product-environment`), pattern (`gnome`), vérifiés dans les
  dépôts (Tumbleweed, comps de Fedora 41) ; un nom inconnu arrêterait l'installation. Dans l'assistant, la liste et le nom de
  l'ordinateur sur une ligne, compte et mot de passe sur la suivante (demande de l'utilisateur). Les six bureaux vérifiés
  jusqu'à l'écran de connexion sur Fedora 45 et Tumbleweed, GNOME et Xfce sur Debian.
  **Agent invité hors de SELinux** (Fedora, openSUSE) : qemu-ga y tourne dans `virt_qemu_ga_t`, et ce qu'il lance ne
  voyait pas `/home` ni ne chargeait `9pnet_fd` (dossiers partagés, dépôt et récupération en échec). Seul ce domaine est
  rendu permissif (`typepermissive`, module CIL chargé par `semodule` après l'installation, `UnconfinedAgent`) ; SELinux
  reste appliqué au reste. **openSUSE bloque en plus `guest-exec`** (`--block-rpcs` dans son unité) : surchargé par
  `/etc/systemd/system/qemu-guest-agent.service.d/winqemu.conf`. Un invité installé à la main a ces deux limites jusqu'à
  « Installer les outils invités », qui les lève (voir Outils invités, agent bridé).
  **Nom de l'ordinateur modifiable** dans l'assistant (toutes les installations automatiques, Windows compris) : tiré du nom
  de la machine et le suivant tant qu'on ne le change pas ; règle NetBIOS pour Windows (15 caractères, pas que des chiffres),
  une étiquette DNS pour Linux (`WhyComputerNameIsRefused`, `WhyHostnameIsRefused`).
  - **Debian** (debian-installer) : `preseed.cfg` **ajouté à l'initrd**, second cpio newc gzip à la suite de celui de l'ISO
    (le noyau les déballe l'un après l'autre ; méthode documentée par Debian), lu par d-i avant sa première question.
    Ligne `priority=critical --- quiet`, pas de clé. GNOME ou `standard, ssh-server`.
  - **Fedora** (Anaconda) : `ks.cfg` sur une clé vvfat **OEMDRV**, `inst.ks=hd:LABEL=OEMDRV:/ks.cfg`, et
    `inst.stage2=hd:LABEL=…` **lu dans le grub.cfg de l'ISO** (étiquette de volume complète ; Windows la montre tronquée, `Fedora-E-dvd-x86`).
    **ISO d'installation seulement** (Everything, Server : `images/install.img`) : une ISO Live s'installe en se copiant,
    sans kickstart ; elle a pourtant `images/pxeboot`, d'où le test (`IsoFileReader.Contains`, sans lire le gigaoctet).
    **`xconfig --startxonboot`** avec bureau (`skipx` sinon) : installé en mode `text`, Workstation démarrait sur une
    connexion texte. **`-plasma-setup`** pour KDE : l'assistant de premier démarrage de Plasma (obligatoire dans ses groupes
    comps, hors de portée de `firstboot --disable`) proposait de créer un compte qui existait déjà. **Un `%pre` attend le
    réseau et le DNS** (`FedoraWaitForNetwork` : `nm-online`, puis `mirrors.fedoraproject.org` qui répond deux fois) : depuis
    l'ISO netinst de Fedora 45, Anaconda préparait ses sources une seconde après que NetworkManager avait monté la carte, et
    sa toute première recherche de nom échouait (« Could not resolve host », 3 ms après « Using degraded feature set UDP
    instead of UDP+EDNS0 for DNS server 10.0.2.3 » de systemd-resolved) : arrêt sur « Erreur lors de la configuration des
    dépôts ». Juste après, et depuis un shell, la résolution marchait toujours. `ip=dhcp` sur la ligne du noyau n'y
    changeait rien. **L'attente ne suffisait pas** (11/11/2026, deux installations sur deux arrêtées sur la même erreur) :
    une ligne `network` qui configure la carte (`--bootproto=dhcp`, avec ou sans `--activate`) la fait **réactiver par
    Anaconda après les `%pre`** (journal de systemd-resolved : liste DNS vidée 2 s après la fin du `%pre`, sources
    préparées 1 s plus tard). La ligne ne porte donc plus que **`--hostname`** : NetworkManager a déjà la carte en DHCP,
    dans l'installateur comme dans le système installé (vérifié : nom d'hôte, carte connectée, résolution de noms).
    Vérifié de bout en bout (Xfce, 09/11/2026) : 9 min sans fenêtre, sources préparées 7 s après le début du `%pre`.
    Diagnostic sans agent : un second QMP dans les arguments supplémentaires, touches envoyées par `send-key` dans le
    shell d'Anaconda (onglet tmux 2 : `Ctrl+B` puis `N`), positions AZERTY, et `screendump`.
  - **openSUSE** (linuxrc, YaST) : `autoinst.xml` sur la clé OEMDRV, `autoyast=label://OEMDRV/autoinst.xml`. L'ISO NET de
    Tumbleweed utilise encore YaST (pas Agama). **Produit exigé** (`<products>`) : un même support en propose plusieurs
    (openSUSE, MicroOS, Aeon, Kalpa), sinon « None or wrong base product » ; lu dans `media.1/products`. **Disques de moins de 4 Gio exclus** (`skip_list`) :
    sans disque nommé, AutoYaST prenait la clé OEMDRV (`sda`, avant `vda`) et s'arrêtait sur « Impossible de suggérer un
    partitionnement ». Reconnue par
    `media.1/media` : l'arbre ISO 9660 primaire ne sait pas nommer `.treeinfo` (point initial). SLE refusé (enregistrement).
    **`kexec=3`** : le dépôt de Tumbleweed avance, et une ISO de quelques jours n'a plus le noyau de ses paquets ; linuxrc
    s'arrêtait sur « To use the selected repository a matching boot image is needed. Download it now and restart? ». 3 le
    charge sans demander, seulement si nécessaire (source de linuxrc, `install.c`). **DVD de Tumbleweed** (`media.1/media`
    le nomme `openSUSE-Tumbleweed-DVD-…`) : il ne porte qu'une sélection de paquets, sans `spice-vdagent`, et AutoYaST
    s'arrêtait sur « Ces paquets n'ont pas pu être trouvés… : spice-vdagent » (DVD du 30/10/2026) ; le dépôt en ligne
    `tumbleweed/repo/oss` est ajouté (`<add-on>`, `OpenSuseOnlineRepository`). L'ISO NET installe déjà depuis lui. Et
    **`ifcfg=*=dhcp`** sur la ligne du noyau : depuis le DVD, linuxrc laissait le réseau éteint, et YaST s'arrêtait à 86 %
    sur « Réseau non fonctionnel » (le dépôt en ligne aurait été injoignable). **Vérifié de bout en bout** sur le DVD du
    30/10/2026 avec KDE (09/11/2026) : 12 min sans aucune fenêtre, `spice-vdagent` installé, `repo-oss` à côté du DVD,
    réseau en place, écran de connexion de Plasma. **Disposition graphique** : `<keymap>` ne règle
    que la console ; Xfce (LightDM) montrait « us » à la connexion. SDDM (LXQt) affiche encore « us » : c'est le clavier virtuel de X, qui ne prend celui du vrai clavier (« fr », vérifié par `xkbcomp -i`) qu'à la première touche. `/etc/X11/xorg.conf.d/00-keyboard.conf` écrit par `<files>`.
  - Suivi des essais par `winqemu capture <machine> [fichier]` (nouveau, comme `captureScreen` de vmrun : `screendump` de
    la session, sans dépendre de l'écran).
- **`-accel whpx` sans `kernel-irqchip`** : par défaut QEMU prend l'APIC de l'hyperviseur s'il en a un, sinon le
  sien. Forcé à `off` (l'ancienne règle, de l'époque où le défaut refusait de démarrer), **un invité Windows à plus
  d'un vCPU se figeait sur la roue de démarrage**, installation de Windows 11 comprise ; 1 vCPU démarrait, et tout
  nombre de vCPU avec l'APIC de l'hyperviseur. Trouvé par sondes QMP sans affichage (`screendump`) : ni le modèle
  CPU, ni CET, ni la topologie, ni x2APIC, ni HPET, ni i440fx n'y changeaient rien. Ubuntu et Debian démarrent
  pareil avec 4 vCPU.
- **qemu-system n'ouvre aucun chemin non ASCII** (qemu-img, si) : il passe sa ligne de commande en UTF-8 puis ouvre
  les fichiers par l'API ANSI. Une machine importée sous « Debian (importée) » s'arrêtait sur « Could not open
  '…\Debian (importÃ©e)\firmware-vars.fd': Le chemin d'accès spécifié est introuvable ». D'où des **dossiers de VM en
  ASCII** (`VmLibrary.SanitizeFolderName` : accents repliés, le reste en `_` ; le nom affiché garde ses accents), et
  tout chemin passé à QEMU par `QemuArgumentBuilder.QemuReadablePath` : un chemin accentué (ISO dans
  « Téléchargements », machine plus ancienne) devient son nom court 8.3, ASCII, vérifié ; un volume sans noms courts
  le laisse tel quel. La sortie de QEMU est lue en UTF-8 (`VmSession.StartProcess`).
- « failed to get xsave state » sur la sortie d'erreur : deux par vCPU secondaire au démarrage, sans effet.
- **QEMU figé par SDL quand vmmouse s'active : `vmport=off`** sur les machines PC (`QemuArgumentBuilder.DisablesVmport`).
  Vu sur le Ryzen 9 5900XT de l'utilisateur (02-05/11/2026) : openSUSE Tumbleweed installé figeait à l'ouverture de la
  session graphique, QEMU ne répondait plus et WinQEMU, dont la fenêtre contient la sienne, figeait avec lui (WER
  `MoAppHangXProc`). **L'invité n'y est pour rien** : avec `-display none`, il atteint le bureau KDE. Piles du processus figé
  (`StackWalk64` de dbghelp, noms exportés seulement) : un fil vCPU, qui tient le verrou global de QEMU pendant un accès
  d'E/S, appelle `SetWindowTextW` → `SendMessageW` vers la fenêtre SDL ; le fil principal, qui la possède, attend ce verrou.
  - Chemin : le pilote vmmouse de l'invité (port VMware de `vmport`) fait enregistrer à QEMU une souris **relative** et
    l'activer. Tablette USB déjà active, le mode passe d'absolu à relatif : `sdl_mouse_mode_change` → `sdl_grab_end` →
    titre de la fenêtre, **sans condition de focus** (une console intégrée sans focus y passe aussi). Dans QEMU 11, l'avis de
    mode part à chaque activation d'une souris, même sans changement. Sans tablette, pas d'état absolu à quitter : rien.
  - Mesuré sur une copie `-snapshot`, ligne de WinQEMU (tablette USB, SDL) : 3 blocages sur 3 vers 58 s ; `vmport=off` :
    2 passages sur 2 jusqu'au bureau. Les effets attribués plus tôt aux options CET ou à `kernel-irqchip` n'en sont pas :
    ce sont des variations de chronologie. **Probablement pas propre à AMD** (dépend du pilote vmmouse de l'invité) ; non
    vérifié sur Intel.
  - **Autre chemin, non couvert** (WinQEMU n'utilise pas ce périphérique) : `virtio-tablet-pci` activée par l'invité fait
    passer en absolu depuis le vCPU, et `absolute_mouse_grab` change le titre **si la fenêtre SDL a le focus** et que le
    pointeur y est (QEMU seul, fenêtre de premier plan : bloqué à 50 s).
  - Écarté : rendre la tablette active dès le démarrage par `mouse_set` (moniteur) : l'avis de vmmouse figeait quand même,
    et un invité sans pilote de tablette perdrait sa souris jusqu'à son chargement.
  - Pour diagnostiquer un autre blocage de QEMU : `query-mice` toutes les 100 ms (quelle souris est active, et quand), et
    les piles de ses fils. Ce PC de test n'a ni Python ni SDK .NET : QMP en PowerShell, outil de piles compilé par le
    `csc.exe` du .NET Framework (C# 5).
- **Un QEMU figé ne fige plus WinQEMU sans issue** (`Core.Interop.HungConsoleWatch`, créé par `MainWindow.HungConsole`) :
  un fil à lui envoie `WM_NULL` (`SendMessageTimeout`, `SMTO_ABORTIFHUNG`) à chaque console intégrée, affichée ou garée,
  chaque seconde. Sans réponse depuis 10 s : **TaskDialog Windows sans propriétaire, sur un fil à elle** (le fil de
  l'interface est sans doute celui qui est bloqué ; un propriétaire ferait désactiver sa fenêtre, donc un appel vers lui ;
  un ContentDialog s'afficherait par ce fil-là ; une fenêtre WinUI sur un autre fil lirait les ressources de l'application
  d'un fil à l'autre, non vérifié quand le principal est bloqué). Liens « Éteindre la machine » / « Attendre », Attendre par
  défaut et pour Échap, au premier plan (`HWND_TOPMOST`), **fermée d'elle-même quand QEMU repart** (`IsHungAppWindow`
  chaque seconde), reposée 1 min après « Attendre » (et elle le dit : sans le délai, on croyait qu'elle ne revenait plus).
  « Éteindre » → `VmSession.ForcePowerOff` : `Kill` sans QMP (ses commandes tournent sur le fil bloqué) ni verrou de
  transition, et la machine finit **Arrêtée**, pas Erreur. Tuer QEMU libère l'interface. Common Controls 6 déclaré dans
  `app.manifest` (sans lui, pas de `TaskDialogIndirect`). **Piège** : `TD_WARNING_ICON` vaut `0xFFFF`
  (`MAKEINTRESOURCE(-1)`), pas `new IntPtr(-1)`, lu comme une chaîne : WinQEMU mourait d'une violation d'accès au premier
  dessin de l'icône. Vérifié sur une VM jetable sans disque dont le processus QEMU était suspendu (`NtSuspendProcess`) :
  question au bout de 12 à 15 s, fermeture à la reprise, Éteindre puis redémarrage ; plusieurs VM essayées par l'utilisateur.
- **Une machine en Erreur redémarre** (QEMU planté, démarrage raté, processus parti) : `VmSession.StartAsync` refusait tout
  état autre qu'Arrêtée, et répondait « déjà démarrée » jusqu'au redémarrage de WinQEMU, alors que l'interface proposait
  Démarrer.

## Pièges WinUI 3

- **`IsEnabled` est déclaré par `Control`, pas par `Panel`.** Le lier sur un `StackPanel`/`Grid` fait
  sortir `XamlCompiler.exe` en erreur **sans aucun message** ; seule une bissection le trouve. Envelopper
  le panneau dans un `ContentControl`.
- **Un HWND enfant passe toujours au-dessus de XAML** et n'est pas rogné par lui : masquer la console
  (`Suspend`) avant tout dialogue, et la rogner à son panneau (`SetWindowRgn`) quand l'image est plus grande
  (« Taille réelle »). **Menus et volets** de la barre d'outils et du plein écran : dans une fenêtre à eux
  (`ShouldConstrainToRootBounds = false`, `MainWindow.ShowAboveConsole`), que Windows dessine au-dessus de la
  console ; la masquer faisait disparaître l'invité pendant qu'on réglait son volume. Un clic ailleurs dans la
  fenêtre ferme le menu **et** atteint ce qu'il vise (`OverlayInputPassThroughElement`, sinon Son ouvert, un clic
  sur USB ne faisait que fermer Son) ; un clic sur le bouton du menu ouvert ne fait que le fermer
  (`OnlyClosedItsMenu`) ; un clic dans la console, que XAML ne voit pas, le ferme par `ConsoleClicked`.
- **Barres d'outils en icônes** (demande de l'utilisateur ; écran 1920 x 1200 à 150 %, soit 1280 px pour la fenêtre) :
  `ToolbarIconButton` (bouton simple, 44 x 40, nom accessible par `AutomationProperties.Name` ; un AppBarButton compact
  garde la place de son libellé sous l'icône, qui se retrouvait plus haut que les boutons de la bibliothèque) et
  `FullScreenIconButton` (« Quitter le plein écran » compris, en couleur d'accent) ;
  l'info-bulle donne le nom en gras puis ce que fait la commande (`MainWindow.NameInTooltips`, construite à
  l'ouverture pour suivre Pause/Reprendre). Les actions d'un disque aussi (`DiskIconButton`) : avec leurs libellés,
  le nom du fichier se lisait « disk0.qco ». En dessous de la largeur nécessaire, repli dans « Plus »
  (`FitMachineToolbar` : Cloner, Exporter, Supprimer, puis Son et les fichiers).
- **Mise en page vérifiée à 1280, 1024 et 800 px** (captures `PrintWindow` d'un script rendu *per-monitor aware* :
  sinon Windows multiplie les tailles demandées par 1,5). `ApplyAdaptiveLayout` : Ressources sous le résumé en
  dessous de 1200 px (à 1024 le résumé, coincé, rognait disques, listes et boutons), liste à 280 px en dessous de
  1000. **Taille minimale de la fenêtre 800 x 600** (`ApplyMinimumSize`, à reposer après le plein écran : le
  présentateur par défaut revient neuf). Dialogues larges (assistant, Paramètres, réseaux) : `DialogWidth.Fit`
  (l'assistant de 700 px débordait à 800). Rangées de boutons qui peuvent manquer de place : `Controls.WrapPanel`
  (WinUI n'en a pas), et seules les actions applicables (Chiffrement).
- **Thème** (Paramètres : Windows, clair, sombre ; `AppSettings.Theme`, appliqué aussitôt par `MainWindow.ApplyTheme` :
  `RootGrid.RequestedTheme`, barre de titre, et chaque dialogue au moment de l'afficher — un popup est hors de l'arbre de
  la fenêtre). **`Application.Current.Resources` répond dans le thème de Windows, pas dans celui de la fenêtre** : en
  clair sur un Windows sombre, l'arbre des instantanés écrivait « État d'origine » blanc sur blanc. Ce qui est dessiné
  en code prend ses pinceaux par `Controls.ThemeBrushes.Get(élément, clé)` (dictionnaires de thème de l'élément) et est
  redessiné au changement de thème. Vérifié en clair : résumé, sections, arbre, Paramètres, menu Son, graphiques.
- **Une InfoBar fermée compte encore dans l'espacement d'un StackPanel** (hauteur nulle, mais `Spacing` de part et
  d'autre) : les sections du résumé n'étaient pas également espacées, et le panneau des notifications laissait une bande
  vide sous la barre de titre. Toute InfoBar liée par `IsOpen` l'est aussi par `Visibility`.
- **`x:DataType` obligatoire sur un DataTemplate qui lie ses textes par `{x:Bind t:…}`** : sans lui, la liaison
  compile mais ne s'exécute jamais — boutons et cases des disques sans texte. `LanServiceItem` n'est plus imbriqué
  dans `MainViewModel` pour cela (XAML ne nomme pas un type imbriqué).
- **ComboBox éditable dont la liste arrive après la valeur** (type de machine, modèle de processeur : la liste vient de
  QEMU) : remplacer ses éléments vide son texte, sans rien écrire dans la liaison. Une valeur absente de la liste (le
  modèle par défaut, `qemu64,+sse4.1,…`) s'affichait donc vide, la machine la gardant. Liées par `VmHardwareViewModel`
  (`MachineType`, `CpuModel`), qui redonne le texte une fois la liste là et ignore ce que la boîte écrit pendant le
  remplacement. **Ne jamais basculer `IsEditable`** d'une boîte liée en deux sens : elle a écrit des valeurs vides dans
  la machine, qui ont été enregistrées (vu sur Debian pendant un essai, valeurs remises à la main).
- **Visibilité réelle** : tester la visibilité de tous les ancêtres, pas seulement de l'élément (la vue
  Résumé masque la console en repliant son conteneur).
- **Boutons de `CommandBar`** : ils ne prennent pas le focus par défaut, donc une `NumberBox` en cours de
  saisie n'est pas validée. `AllowFocusOnInteraction="True"` sur ceux qui consomment la configuration.
- **QEMU lancé avec `SDL_WINDOWS_DPI_AWARENESS=permonitorv2`** (`VmSession.StartProcess`). Sans cela sa
  fenêtre SDL est *DPI-unaware* (mesuré : niveau 0) et devient l'enfant d'une app *per-monitor* (niveau 2) :
  à 150 %, le pointeur de l'invité tombait à une largeur de flèche du curseur affiché, et l'image était
  dessinée aux 2/3 puis réagrandie (floue en « Taille réelle »).
- **L'invité suit la taille de la fenêtre** : SDL transmet sa taille comme mode préféré (`set_ui_info`), et
  un invité virtio-gpu l'adopte (Ubuntu : fenêtre 1898×1144 → invité 1898×1144). Avant le correctif DPI,
  cette taille arrivait divisée par 1,5. **SDL garde lui-même les proportions** (bandes noires dans sa
  fenêtre) : « Ajuster » lui donne donc d'abord tout le panneau (un ancien recadrage fondé sur une taille
  d'invité périmée, 640×480, enfermait l'invité en 4:3).
- **QEMU 11.1 ne sait pas pointer dans une image mise à l'échelle** : SDL convertit le pointeur en coordonnées
  de l'image (bandes comprises), puis QEMU les rapporte à la taille de la **fenêtre** au lieu de celle de
  l'image. Mesuré par la trace `input_event_abs` (invité 1280×800, fenêtre 1578×944 : le centre arrive à 81 % et
  85 %). Le pointeur n'est juste que fenêtre = image. Dès que l'image est mise à l'échelle, **WinQEMU pointe à
  la place de QEMU** :
  `PointerOverlay`, une fenêtre enfant à nous, superposée (couche à alpha 1/255 : invisible mais touchée par la
  souris ; à 0 elle ne l'est plus), prend la souris et envoie la position calculée (bandes comprises,
  `PointerEvent.ToAbsolute`) et les boutons par QMP `input-send-event` (`abs`, `btn`), mouvements fusionnés
  le temps d'un aller-retour. La fenêtre de QEMU est gardée juste en dessous (`SetBounds(..., below)`). Prix :
  le curseur est la flèche de Windows (les formes de l'invité n'existent que dans la fenêtre de QEMU). À taille
  native, la couche est cachée et QEMU garde la souris.
- **« Ajuster » donne toujours tout le panneau à QEMU**, même à un invité d'une autre résolution : la taille de la
  fenêtre est le mode préféré de l'invité. Windows avec le pilote d'affichage de virtio-win l'adopte **au
  chargement du pilote, au démarrage**, puis ne suit plus la fenêtre. Une ancienne règle montrait à taille
  native un invité qui ne suivait pas la fenêtre ; pendant le démarrage elle réduisait donc la fenêtre aux
  1280×800 du microprogramme, et Windows démarrait en 1280×800 même en plein écran. Windows sans pilote d'affichage reste en
  1280×800 (le mode GOP d'OVMF) : la résolution préférée annoncée par EDID (`virtio-vga,xres=,yres=`) est
  ignorée par le microprogramme comme par l'installation de Windows (vérifié).
- **La résolution d'un invité Windows suit la console** (`WindowsGuestDisplay`, comme les VMware Tools) : seule la
  session de l'utilisateur peut changer la résolution, pas l'agent (SYSTEM, session 0). L'agent compile une fois par
  démarrage un petit programme avec le .NET Framework de Windows (`Add-Type -OutputAssembly`, sans fenêtre, rien de
  téléchargé) dans `%ProgramData%\WinQEMU`, et enregistre la tâche `WinQEMU-Resolution` du groupe Users ; à chaque
  changement de taille en « Ajuster » ou plein écran (`VmConsoleHost.PreferredSizeChanged`, 800 ms d'attente), le
  shell écrit la taille dans `HKLM\SOFTWARE\WinQEMU\Resolution` et lance la tâche par `cmd`/`schtasks` (0,6 s ;
  nom sans espace, l'agent requote les arguments). **Le pilote d'affichage de virtio-win n'offre qu'une liste fixe**
  (modes standard + la taille de la fenêtre au démarrage) et n'en ajoute pas ensuite : le programme prend la taille
  exacte si elle existe (1920×1080 en plein écran), sinon le plus grand mode qui tient (1400×800 → 1280×800), que la
  console met à l'échelle. Changement non enregistré dans Windows (sinon il primerait sur la taille de la fenêtre au
  démarrage suivant). Vérifié sur le Windows 11 de l'utilisateur.
- **Un clic dans la console ne donne pas le clavier à XAML** : il va au processus de QEMU, et le focus restait
  sur le dernier contrôle utilisé (la liste « Ajuster » prenait les touches : impossible de saisir un nom dans
  l'installation de Windows). `ChildClickWatcher` sous-classe la fenêtre du shell et reçoit `WM_PARENTNOTIFY`,
  que Windows envoie au parent même quand l'enfant est d'un autre processus ; un clic dans la console appelle
  `FocusGuest`. « Étirer » n'existait plus en
  pratique et a disparu. Seule « Taille réelle » a besoin de la résolution, relue toutes les 5 s et après
  chaque redimensionnement (une adoption de mode par l'invité ne redimensionne pas la fenêtre de QEMU).
- **Barre du plein écran** : XAML ne peut pas passer au-dessus de la console ; le **rectangle de la barre** est
  retiré de la région visible de la fenêtre native et de la couche du pointeur (`VmConsoleHost.Overlay`,
  `NativeMethods.CreateRegion` : région moins le trou). Une bande de toute la largeur de l'écran masquait l'invité de
  part et d'autre de la barre (demande de l'utilisateur : « seulement le menu »). Barre flottante à 8 px du haut, coins
  arrondis comme le reste de l'application, trou aux mêmes coins (`CreateRoundRectRgn`) : carré, ses coins restaient noirs. Le pointeur est
  sondé (`GetCursorPos`) : au-dessus de l'invité il appartient à la fenêtre de QEMU, XAML n'en sait rien.
- **Tab** : la navigation au clavier de WinUI déplace le focus même si `PreviewKeyDown` est marqué traité,
  et la touche arrive **sans scancode**. La console annule les pertes de focus clavier et retrouve le
  scancode par `MapVirtualKey`.
- **AltGr** arrive comme un Ctrl gauche factice suivi d'Alt droit : `AltGrFilter` supprime le Ctrl.
- **Touche maintenue : la répétition de Windows est transmise** (`Input.KeyRepeat`), sauf modificateurs et verrous.
  N'envoyer que l'appui et le relâchement suffisait sous un bureau (le compositeur répète lui-même), pas dans une console
  texte Linux, dont le pilote PS/2 attend que le clavier renvoie le code : Bas maintenu dans archinstall ne descendait que
  d'une ligne. Un bureau ne répète pas deux fois : le noyau signale ces codes comme des répétitions, que le compositeur ignore.
- **Clic dans le vide d'un `ScrollViewer`** : il prend le focus et, s'il ne peut pas le garder, le passe à son
  premier descendant focalisable (la zone du nom de la VM se sélectionnait). `IsTabStop="True"` sur le
  `ScrollViewer` le lui fait garder. Pas `AllowFocusOnInteraction="False"` : il atteint tout le contenu et
  plus aucun champ ne se laisse cliquer.
- Un raccourci clavier sur la grille racine s'affiche en infobulle partout :
  `KeyboardAcceleratorPlacementMode="Hidden"`.

## Diagnostiquer une VM invitée qui plante

- Le journal de la VM dans l'app montre la ligne de commande exacte : la rejouer à la main, de
  préférence sur une copie du disque et avec `-snapshot` pour ne rien écrire.
- **Panique noyau d'un invité encore allumé** : vidage mémoire complet du processus QEMU
  (`MiniDumpWriteDump`, la RAM de l'invité y est) puis recherche des lignes de noyau formatées
  (`segfault at`, `potentially unexpected fatal signal`). C'est ce qui a trouvé la cause CET.
- Lire le disque d'un invité Linux arrêté : démarrer le noyau de l'ISO Ubuntu avec
  `boot=casper init=/bin/bash console=ttyS0`, disque attaché, et `mount -o ro,noload`.
- Un shell QMP de plus pour observer une VM lancée par l'app : `-qmp tcp:127.0.0.1:<port>,server=on,wait=off`
  dans les arguments supplémentaires (QEMU accepte plusieurs moniteurs ; celui de l'app est exclusif).

## Conventions

- **Interface en anglais et en français** (voir Langues), **code, commentaires et journal de la VM en anglais.** Un
  texte visible ne s'écrit jamais en dur : une entrée dans `Text\*.resx` et son `.fr.resx`.
- Les commentaires expliquent **pourquoi**, et citent le symptôme observé quand un choix vient d'un bug
  (« the shell then shows it as paused »). Garder ce niveau de détail : c'est ce qui évite de
  réintroduire un piège.
- Une correction de la ligne de commande QEMU s'accompagne d'un test dans `WinQEMU.Core.Tests`
  (`Fixtures.Build` / `Fixtures.Value`).
- Logo : modifier la géométrie dans `tools/Generate-Logo.ps1` et `WinQEMU/Assets/Logo.svg`, puis
  `pwsh ./tools/Generate-Logo.ps1`. Couleur fixe `#1F6FEB`, tracé monochrome (écran formant un Q).
