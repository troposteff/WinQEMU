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
- Après un déploiement, vérifier que la DLL installée contient bien le changement plutôt que de supposer.

## Décisions d'architecture

Prises avec l'utilisateur ; ne pas les remettre en cause sans raison nouvelle.

- **Console : fenêtre SDL de QEMU rattachée à l'app.** QEMU tourne avec `-display sdl,gl=off`,
  le shell retrouve son HWND et le `SetParent()` dans sa fenêtre. VNC et SPICE ont été écartés **pour
  l'affichage** (pas de client .NET mûr, un client RFB complet serait à écrire).
- **Clavier par QMP, jamais par la fenêtre.** Une fenêtre SDL enfant n'atteint jamais l'état de focus
  qu'exige QEMU. `VmConsoleHost` capte les touches en XAML et les envoie en scancodes AT set 1
  (`input-send-event`). La souris passe nativement. Ne jamais donner le focus Win32 à la fenêtre SDL :
  XAML cesserait de livrer les touches.
- **Console d'une VM non affichée : garée, pas rendue au bureau.** Rendue au bureau, SDL la réaffiche
  seul au premier changement de mode vidéo de l'invité. Elle est mise en enfant d'une fenêtre cachée de
  l'app et reprise telle quelle au retour (`QemuConsoleWindow.Park/Unpark`).
- **Presse-papiers : VNC utilisé comme simple canal de presse-papiers** (texte uniquement). L'interface
  SDL de QEMU n'a aucun support du presse-papiers, et le serveur VNC est le seul pair de presse-papiers
  côté hôte dans le build Windows. Côté invité : chardev `qemu-vdagent` sur le port virtio-serial
  `com.redhat.spice.0` (il faut `spice-vdagent` dans l'invité, et une session graphique). Côté hôte :
  `-vnc 127.0.0.1:N,password=on`, mot de passe aléatoire posé par QMP ; `VncClipboardClient` ne parle
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
  matériel à l'installation.
- `kernel-irqchip=off` est obligatoire, sans quoi QEMU refuse de démarrer.
- « failed to get xsave state » sur la sortie d'erreur : deux par vCPU secondaire au démarrage, sans effet.

## Pièges WinUI 3

- **`IsEnabled` est déclaré par `Control`, pas par `Panel`.** Le lier sur un `StackPanel`/`Grid` fait
  sortir `XamlCompiler.exe` en erreur **sans aucun message** ; seule une bissection le trouve. Envelopper
  le panneau dans un `ContentControl`.
- **Un HWND enfant passe toujours au-dessus de XAML** et n'est pas rogné par lui : masquer la console
  (`Suspend`) avant tout dialogue ou menu, et la rogner à son panneau (`SetWindowRgn`) quand l'image est
  plus grande (« Taille réelle »).
- **Visibilité réelle** : tester la visibilité de tous les ancêtres, pas seulement de l'élément (la vue
  Résumé masque la console en repliant son conteneur).
- **Boutons de `CommandBar`** : ils ne prennent pas le focus par défaut, donc une `NumberBox` en cours de
  saisie n'est pas validée. `AllowFocusOnInteraction="True"` sur ceux qui consomment la configuration.
- **Tab** : la navigation au clavier de WinUI déplace le focus même si `PreviewKeyDown` est marqué traité,
  et la touche arrive **sans scancode**. La console annule les pertes de focus clavier et retrouve le
  scancode par `MapVirtualKey`.
- **AltGr** arrive comme un Ctrl gauche factice suivi d'Alt droit : `AltGrFilter` supprime le Ctrl.
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
