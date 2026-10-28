# WinQEMU

Interface graphique Windows (WinUI 3) au-dessus de QEMU, visant le confort de VMware ou Hyper-V :
bibliothèque de VM, assistant de création, console intégrée, instantanés, accélération WHPX.

## Structure

| Projet | Rôle |
|---|---|
| `WinQEMU.Core` | Tout ce qui ne dépend pas de l'interface : modèles (`VmConfig`…), construction de la ligne de commande (`QemuArgumentBuilder`), cycle de vie d'une VM (`VmSession`), QMP (`QmpClient`), `qemu-img`, détection de QEMU, interop Win32 de la console. |
| `WinQEMU` | Shell WinUI 3 empaqueté MSIX : `MainWindow`, view-models, dialogues, `VmConsoleHost` (console intégrée), `ClipboardBridge`. |
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
  `shell:AppsFolder\cfde84db-a7f2-4241-ae31-4fd9bc34967b_xxm26tvjas0jr!App`.
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

- **Arborescence** (`SnapshotTree`, dessinée par `Controls.SnapshotTreeView`, comme le gestionnaire de VMware) :
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
- **Positions de touches, pas caractères** : l'invité doit avoir la disposition de ce PC. Écart vu sur Debian en
  « fr latin9 », où AltGr+2 n'est pas une touche morte : « ~ » seul (touche morte + espace sous Windows) y devient « ~ ».
  Tout le reste est juste.
- Vérifié sur la console texte d'une copie jetable de Debian 13 (relu par `/dev/vcsu1` via l'agent) et dans Exécuter
  d'un Windows 11. Le presse-papiers n'était pas accessible depuis l'environnement de test : bouton vérifié seulement
  jusqu'à « Rien à taper ».

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
- **Attendre les machines vivantes** (`VmManager.LiveSessions`), pas `RunningSessions` : une machine priée de
  s'arrêter passe « Arrêt en cours », que `RunningSessions` exclut. Les premières versions de « Arrêter et quitter »
  la croyaient donc arrêtée aussitôt et coupaient QEMU en quittant (Ubuntu « arrêté » en 3 s). Une machine en pause est
  reprise avant le bouton d'alimentation : figée, elle ne l'entendrait pas.

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
- **Protocole** invité → hôte : `T` total, `D` dossier, `F` nom + taille + date puis morceaux d'1 Mio, `E` fin, `X`
  erreur. 20 s sans rien reçu : `guest-exec-status` dit si l'émetteur est mort.
- **Noms** : un nom de premier niveau déjà pris est numéroté ; les caractères interdits par Windows (`:`, `?`…) et
  points ou espaces finaux sont remplacés (`WindowsName`). Lien symbolique vers un fichier : son contenu est copié ;
  vers un dossier : ignoré (boucles). Annulé ou échoué : ce que la copie a créé est supprimé.
- Vérifié sur des copies jetables : Ubuntu (`/usr/share/doc/bash`, empreintes identiques) et Windows 11 (5/5) ;
  dialogue ouvert dans l'app sur Debian. Émetteur Windows testé sur l'hôte avec un tube nommé (`GuestFilePullTests`).

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
- **Barre d'outils trop large** (`FitMachineToolbar`) : elle passait sous Réseaux virtuels et Paramètres à
  1440 px. Replis successifs dans « Plus » : Cloner, Exporter, Supprimer, puis Son et Envoyer.
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
- **Barre du plein écran** : XAML ne peut pas passer au-dessus de la console ; la bande de la barre est
  retirée de la région visible de la fenêtre native (`VmConsoleHost.TopOverlayHeight`). Le pointeur est
  sondé (`GetCursorPos`) : au-dessus de l'invité il appartient à la fenêtre de QEMU, XAML n'en sait rien.
- **Tab** : la navigation au clavier de WinUI déplace le focus même si `PreviewKeyDown` est marqué traité,
  et la touche arrive **sans scancode**. La console annule les pertes de focus clavier et retrouve le
  scancode par `MapVirtualKey`.
- **AltGr** arrive comme un Ctrl gauche factice suivi d'Alt droit : `AltGrFilter` supprime le Ctrl.
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

- **Interface en français, code et commentaires en anglais.**
- Les commentaires expliquent **pourquoi**, et citent le symptôme observé quand un choix vient d'un bug
  (« the shell then shows it as paused »). Garder ce niveau de détail : c'est ce qui évite de
  réintroduire un piège.
- Une correction de la ligne de commande QEMU s'accompagne d'un test dans `WinQEMU.Core.Tests`
  (`Fixtures.Build` / `Fixtures.Value`).
- Logo : modifier la géométrie dans `tools/Generate-Logo.ps1` et `WinQEMU/Assets/Logo.svg`, puis
  `pwsh ./tools/Generate-Logo.ps1`. Couleur fixe `#1F6FEB`, tracé monochrome (écran formant un Q).
