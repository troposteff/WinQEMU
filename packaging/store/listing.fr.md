# Fiche du Microsoft Store — français

Champs de Partner Center, Fiches du Store, français (France) : importés par New-ListingImport.ps1, ou collés bloc par bloc. Texte brut : le Store n'affiche pas de Markdown.

## Nom du produit

WinQEMU

## Description

Nécessite QEMU pour Windows, installé à part et gratuit : https://qemu.weilnetz.de/w64/ ou la commande « winget install SoftwareFreedomConservancy.QEMU ». WinQEMU le trouve tout seul et vous guide pour l'installer. WinQEMU est une interface indépendante et non officielle : elle n'est ni affiliée au projet QEMU ni approuvée par lui. QEMU est une marque de Fabrice Bellard.

WinQEMU est une interface Windows au-dessus de QEMU, qui vise le confort de VMware Workstation ou de Hyper-V. Créez, lancez et gérez des machines virtuelles sans taper de ligne de commande, avec l'accélération matérielle de la plateforme d'hyperviseur Windows.

Au premier démarrage, WinQEMU vérifie que ce PC est prêt : QEMU trouvé, accélération matérielle activée, et quoi faire sinon.

Machines virtuelles : une bibliothèque en dossiers imbriqués, un assistant de création, l'installation automatique de Windows 10/11, d'Ubuntu, de Debian, de Fedora et d'openSUSE, l'import depuis VMware, VirtualBox, Hyper-V et OVA, l'export en OVA, les clones complets et liés, les modèles, le démarrage automatique, les machines laissées en arrière-plan.

Console : intégrée ou en plein écran, la résolution de l'invité suivant la fenêtre, un presse-papiers partagé, la saisie du presse-papiers dans l'invité, la capture d'écran, des fichiers déposés sur le Bureau de l'invité, glissés hors de lui vers ce PC ou récupérés depuis lui, le son avec un volume par machine.

Instantanés : hors ligne, à chaud sur les disques, ou avec la mémoire sous émulation ; une arborescence d'instantanés ; des instantanés automatiques.

Disques : créer, agrandir, convertir, compacter et chiffrer (LUKS, AES-256), le mot de passe gardé dans le Gestionnaire d'identification de Windows si vous le souhaitez.

Réseau : NAT avec redirection de ports, réseaux virtuels privés, internes et externes, accès depuis le réseau local en un clic, dossiers partagés, périphériques USB de ce PC.

Ligne de commande : la commande winqemu pilote WinQEMU depuis n'importe quel terminal.

En français et en anglais. Logiciel libre sous licence GNU GPL version 3 : https://github.com/troposteff/WinQEMU

Facultatif : les réseaux virtuels internes passent par le pilote TAP-Windows d'OpenVPN et les réseaux externes par Npcap, tous deux gratuits et installés par vous quand vous créez un tel réseau ; WinQEMU indique où les trouver.

## Description courte

Une interface Windows au-dessus de QEMU : créez, lancez et gérez des machines virtuelles avec l'accélération matérielle, comme avec VMware Workstation ou Hyper-V. Nécessite QEMU pour Windows, installé à part.

## Nouveautés de cette version

Sécurité et fiabilité. Un invité ne peut plus joindre par sa carte réseau les canaux internes de WinQEMU vers QEMU (agent invité, pilotage, dossiers partagés). Les dossiers partagés ne suivent plus les liens qui sortent du dossier que vous avez partagé. Dans un invité Windows, les programmes d'assistance sont rangés dans un dossier que seul le système peut modifier. Une machine importée ne peut plus faire lire à WinQEMU des fichiers hors de son propre dossier.

Vos machines sont mieux protégées : descripteurs et réglages sont écrits de façon sûre et gardés en copie de secours, un compactage raté ne peut plus perdre un disque, « Supprimer avec les fichiers » ne supprime jamais que la machine, et une erreur imprévue ne ferme plus WinQEMU ni ses machines en marche.

Aussi : un invité qui ignore la demande d'arrêt ne reste plus bloqué sur « Arrêt en cours » ; un invité Windows inactif s'arrête à la première demande ; quitter ou arrêter Windows attend toutes les machines ; les touches sont relâchées dans l'invité quand vous changez de fenêtre ; la frappe du presse-papiers peut être arrêtée ; une installation automatique interrompue repart du début ; nombreuses corrections dans les instantanés, les clones, le chiffrement, les réseaux virtuels et la commande winqemu.

## Fonctionnalités du produit

- Bibliothèque de machines virtuelles en dossiers imbriqués
- Assistant de création, avec installation automatique de Windows 10/11, d'Ubuntu, de Debian, de Fedora et d'openSUSE
- Accélération matérielle par la plateforme d'hyperviseur Windows
- Import depuis VMware, VirtualBox, Hyper-V et OVA ; export en OVA
- Clones complets et liés, modèles
- Console intégrée ou en plein écran, résolution de l'invité suivant la fenêtre
- Presse-papiers partagé, fichiers glissés vers l'invité et hors de lui
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

`docs/images/summary-fr.png`, `docs/images/console-fr.png`, `docs/images/wizard-fr.png`, `docs/images/snapshots-fr.png`.
