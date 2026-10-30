# Fiche du Microsoft Store — français

Champs de Partner Center, Fiches du Store, français (France) : importés par New-ListingImport.ps1, ou collés bloc par bloc. Texte brut : le Store n'affiche pas de Markdown.

## Nom du produit

WinQEMU

## Description

Nécessite QEMU pour Windows, installé à part et gratuit : https://qemu.weilnetz.de/w64/ ou la commande « winget install SoftwareFreedomConservancy.QEMU ». WinQEMU le trouve tout seul et vous guide pour l'installer. WinQEMU est une interface indépendante et non officielle : elle n'est ni affiliée au projet QEMU ni approuvée par lui. QEMU est une marque de Fabrice Bellard.

WinQEMU est une interface Windows au-dessus de QEMU, qui vise le confort de VMware Workstation ou de Hyper-V. Créez, lancez et gérez des machines virtuelles sans taper de ligne de commande, avec l'accélération matérielle de la plateforme d'hyperviseur Windows.

Au premier démarrage, WinQEMU vérifie que ce PC est prêt : QEMU trouvé, accélération matérielle activée, et quoi faire sinon.

Machines virtuelles : une bibliothèque en dossiers imbriqués, un assistant de création, l'installation automatique de Windows 10/11 et d'Ubuntu, l'import depuis VMware, VirtualBox, Hyper-V et OVA, l'export en OVA, les clones complets et liés, les modèles, le démarrage automatique, les machines laissées en arrière-plan.

Console : intégrée ou en plein écran, la résolution de l'invité suivant la fenêtre, un presse-papiers partagé, la saisie du presse-papiers dans l'invité, la capture d'écran, des fichiers déposés sur le Bureau de l'invité ou récupérés depuis lui, le son avec un volume par machine.

Instantanés : hors ligne, à chaud sur les disques, ou avec la mémoire sous émulation ; une arborescence d'instantanés ; des instantanés automatiques.

Disques : créer, agrandir, convertir, compacter et chiffrer (LUKS, AES-256), le mot de passe gardé dans le Gestionnaire d'identification de Windows si vous le souhaitez.

Réseau : NAT avec redirection de ports, réseaux virtuels privés, internes et externes, accès depuis le réseau local en un clic, dossiers partagés, périphériques USB de ce PC.

Ligne de commande : la commande winqemu pilote WinQEMU depuis n'importe quel terminal.

En français et en anglais. Logiciel libre sous licence GNU GPL version 3 : https://github.com/troposteff/WinQEMU

Facultatif : les réseaux virtuels internes passent par le pilote TAP-Windows d'OpenVPN et les réseaux externes par Npcap, tous deux gratuits et installés par vous quand vous créez un tel réseau ; WinQEMU indique où les trouver.

## Description courte

Une interface Windows au-dessus de QEMU : créez, lancez et gérez des machines virtuelles avec l'accélération matérielle, comme avec VMware Workstation ou Hyper-V. Nécessite QEMU pour Windows, installé à part.

## Nouveautés de cette version

Choisir un autre modèle de processeur n'efface plus ses options (+sse4.2…, exigées par Windows 11). Nouveau : des réglages avancés du processeur, une case par fonction que QEMU reconnaît. Le logo n'est plus posé sur une vignette sombre.

## Fonctionnalités du produit

- Bibliothèque de machines virtuelles en dossiers imbriqués
- Assistant de création, avec installation automatique de Windows 10/11 et d'Ubuntu
- Accélération matérielle par la plateforme d'hyperviseur Windows
- Import depuis VMware, VirtualBox, Hyper-V et OVA ; export en OVA
- Clones complets et liés, modèles
- Console intégrée ou en plein écran, résolution de l'invité suivant la fenêtre
- Presse-papiers partagé, dépôt et récupération de fichiers
- Arborescence d'instantanés, instantanés à chaud et automatiques
- Chiffrement des disques (LUKS, AES-256)
- Réseaux virtuels privés, internes et externes
- Dossiers partagés et périphériques USB
- La ligne de commande winqemu
- Français et anglais, thèmes clair et sombre

## Termes de recherche

QEMU, machine virtuelle, virtualisation, VM, hyperviseur, WHPX, émulateur

## Copyright et marques

Copyright © 2026 troposteff. QEMU est une marque de Fabrice Bellard ; WinQEMU n'est pas affilié au projet QEMU.

## Conditions de licence supplémentaires

Licence publique générale GNU, version 3 : https://www.gnu.org/licenses/gpl-3.0.html. Code source : https://github.com/troposteff/WinQEMU

## Site web, support, confidentialité

- Site web : https://github.com/troposteff/WinQEMU
- Support : https://github.com/troposteff/WinQEMU/issues
- Politique de confidentialité : https://github.com/troposteff/WinQEMU/blob/main/PRIVACY.md

## Captures d'écran

`docs/images/summary-fr.png`, `docs/images/console-fr.png`, `docs/images/snapshots-fr.png`.
