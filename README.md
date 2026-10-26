# WinQEMU

Interface graphique Windows (WinUI 3) au-dessus de [QEMU](https://www.qemu.org/), qui vise le confort de
VMware ou d'Hyper-V : on crée, lance et pilote ses machines virtuelles sans écrire de ligne de commande.

- **Bibliothèque de VM** : chaque machine est un dossier autonome (descripteur `.wqvm`, disques, NVRAM
  UEFI), déplaçable tel quel.
- **Assistant de création** : système invité, mémoire, processeurs, disques, ISO, firmware BIOS ou UEFI.
- **Console intégrée** dans la fenêtre de l'application ; le clavier est transmis en scancodes, AltGr
  compris, et suit donc la disposition choisie dans l'invité.
- **Presse-papiers partagé** (texte) avec les invités équipés de `spice-vdagent`.
- **Instantanés** : hors ligne, à chaud (disques) et, sous TCG, avec la mémoire.
- **Accélération WHPX** (Windows Hypervisor Platform), avec repli sur l'émulation TCG.

## Prérequis

- Windows 10 1809 ou plus récent, Windows 11 recommandé.
- **QEMU**, installé séparément : WinQEMU ne l'embarque ni ne le télécharge. Il retrouve seul les
  installations courantes (installateur de [qemu.weilnetz.de](https://qemu.weilnetz.de/w64/), Chocolatey,
  Scoop, MSYS2, `PATH`) ; sinon, on lui désigne le dossier. Développé avec QEMU 11.1.
- Pour l'accélération : la fonctionnalité Windows **Plateforme de l'hyperviseur Windows**
  (`HypervisorPlatform`), activée en administrateur puis redémarrage :

  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
  ```

## Compiler et lancer

Il faut le SDK .NET 8.

```powershell
dotnet test WinQEMU.Core.Tests
dotnet build WinQEMU\WinQEMU.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

L'application est empaquetée en MSIX : `WinQEMU.exe` lancé directement ne démarre pas. On enregistre le
paquet sur le dossier de compilation (une seule fois, les compilations suivantes sont prises en compte
sans réenregistrement), puis on le lance depuis le menu Démarrer :

```powershell
Add-AppxPackage -Register WinQEMU\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AppxManifest.xml
```

L'application ouverte verrouille ses fichiers : la fermer avant de recompiler.

## Limites connues sous WHPX

Elles viennent de l'hyperviseur, pas de QEMU ni de WinQEMU :

- **Pas d'instantané mémoire** d'une machine accélérée : seuls les disques sont capturés. Les instantanés
  avec mémoire exigent l'émulation TCG.
- **Pas de Secure Boot ni de TPM** : les invités Windows 11 demandent de contourner le contrôle matériel
  à l'installation.
- Un redémarrage de l'invité sous UEFI relance QEMU à froid (redémarrage à chaud impossible).

## Structure

| Projet | Rôle |
|---|---|
| `WinQEMU.Core` | Modèles, ligne de commande QEMU, cycle de vie des VM, QMP, `qemu-img`, détection de QEMU. |
| `WinQEMU` | Application WinUI 3 : fenêtre principale, dialogues, console intégrée. |
| `WinQEMU.Core.Tests` | Tests xUnit du cœur ; ceux qui appellent le vrai `qemu-img` sont ignorés sans QEMU installé. |

Les choix d'architecture et les pièges de plateforme rencontrés sont détaillés dans
[`CLAUDE.md`](CLAUDE.md).
