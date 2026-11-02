# PureClean

Nettoyeur et optimiseur pour Windows 10 / 11, dans l'esprit de CCleaner, **sans publicité, sans abonnement et sans télémétrie**.

- Aucune dépendance externe : uniquement .NET 8 et les API de Windows.
- PureClean n'envoie aucune donnée. Seule la page « Logiciels » utilise Internet, via winget (l'outil officiel de Microsoft) et les pages GitHub officielles de RustDesk et d'ArtCraft Launcher, et uniquement quand vous le demandez.
- Pas de « version Pro », pas de programme résident, pas de mise à jour forcée.
- Thème clair, sombre ou selon Windows.

## Fonctionnalités

### Accueil
Version de Windows, espace libre de chaque disque (en orange sous 10 %), mémoire utilisée, programmes actifs au démarrage, total libéré et derniers nettoyages. Le bouton **Nettoyage rapide** analyse votre sélection habituelle, annonce le gain, puis nettoie après confirmation.

### Nettoyage
L'analyse ne supprime rien. Le nettoyage demande confirmation. Les cases cochées sont mémorisées.

| Section | Contenu |
|---|---|
| Windows | Fichiers temporaires (plus de 24 h), corbeille, cache Windows Update, optimisation de distribution, rapports d'erreurs, vidages mémoire, anciens journaux ; miniatures, documents récents, cache DNS *(décochés par défaut)* |
| Applications | Caches de Chrome, Edge, Brave, Vivaldi, Opera, Firefox (tous les profils), Discord, VS Code, Teams, Spotify, Steam |
| Confidentialité *(jamais cochée par défaut)* | Historique et cookies des navigateurs Chromium, cookies de Firefox. Refusé tant que le navigateur est ouvert. Mots de passe et favoris jamais touchés. |
| Nettoyage avancé *(jamais coché par défaut)* | Anciennes installations de Windows (`Windows.old`), composants obsolètes (DISM), anciens points de restauration (le plus récent est conservé) |
| Mes dossiers | Vos propres dossiers à vider. Racines de disque, dossiers de Windows et dossiers personnels sont refusés. |

### Analyse disque
Arborescence à la manière de TreeSize : chaque dossier avec sa taille, sa part du dossier parent et son nombre de fichiers, plus la liste des 200 plus gros fichiers. Ouverture dans l'Explorateur et envoi à la corbeille. Les liens et jonctions ne sont pas suivis, et les fichiers OneDrive stockés uniquement en ligne ne sont pas comptés.

### Doublons
Fichiers au contenu strictement identique : comparaison par taille, puis par empreinte du début et de la fin, puis par empreinte SHA-256 complète. Sélection automatique (garder le plus ancien ou le plus récent). Il est impossible de supprimer tous les exemplaires d'un même fichier. Les dossiers de Windows, des programmes, `AppData` et `.git` sont exclus.

### Programmes
Logiciels installés, avec recherche et tri par nom, taille ou date. La désinstallation lance le programme officiel de l'éditeur.

### Logiciels
Installation et mise à jour de logiciels en un clic, à la manière de Ninite, via winget :

- **Installer** : plus de 50 logiciels courants classés par catégorie (dont une section Sauvegarde : Kopia, Duplicati, restic), à cocher. Les logiciels déjà installés sont repérés. L'installation est automatique et silencieuse (réglages par défaut de l'éditeur, sans offre ni logiciel ajouté). Les installateurs proviennent des éditeurs et sont vérifiés par empreinte SHA-256.
- **Liste modifiable** : recherche dans tout le catalogue winget et le Microsoft Store pour ajouter un logiciel, retrait d'un logiciel, retour à la liste par défaut.
- **Profils** : « Exporter la sélection » enregistre votre liste dans un fichier ; sur un Windows neuf, « Importer un profil » puis « Installer la sélection » réinstalle tout. « Créer un profil depuis ce PC » coche les logiciels déjà présents (idéal avant une réinstallation). Les fichiers de `winget export` sont aussi acceptés.
- **RustDesk** et **ArtCraft Launcher** (absents de winget) sont téléchargés depuis leur page GitHub officielle. L'empreinte SHA-256 est vérifiée (celle publiée par GitHub, ou à défaut le fichier `SHA256SUMS.txt` de la version) ; sans empreinte, rien n'est installé. ArtCraft Launcher installe ensuite et tient à jour les applications ArtCraft (FilmCraft, CadCraft…).
- **RustDesk**, en plus : la configuration de votre serveur auto-hébergé est appliquée automatiquement, soit par la chaîne exportée depuis RustDesk, soit par le serveur ID, le relais et la clé. Elle est enregistrée dans vos profils.
- **Mises à jour** : liste des logiciels ayant une nouvelle version, avec un bouton par logiciel et **Tout mettre à jour**.

Chaque identifiant de la liste par défaut a été vérifié dans le dépôt officiel `microsoft/winget-pkgs`. Les logiciels dont l'installateur embarque des offres publicitaires en ont été retirés par Microsoft (FileZilla, par exemple) ; WinSCP le remplace.

### Démarrage
Programmes lancés au démarrage et tâches planifiées tierces. La désactivation utilise le même mécanisme que le Gestionnaire des tâches : rien n'est supprimé.

### Réglages Windows
Optimisations réversibles : confidentialité (télémétrie, publicités, recherche Bing…), réactivité (délai au démarrage, animations, Game DVR…) et Explorateur (extensions, fichiers cachés). Un point de restauration est proposé avant chaque modification.

### Paramètres
- Thème.
- **Nettoyage automatique** quotidien, hebdomadaire ou mensuel : une tâche du Planificateur Windows lance `PureClean.exe --auto-clean`, qui nettoie sans fenêtre la sélection habituelle (sauf le nettoyage avancé), puis se ferme. Sur un portable, uniquement sur secteur. Un journal est disponible.
- Accès aux données locales.

## Ce que PureClean ne fait volontairement pas

- **Pas de « nettoyeur de registre »** : le gain est nul et le risque de casser Windows est réel.
- **Pas d'« optimiseur de mémoire »** : vider la RAM ralentit le PC.
- **Pas de désactivation de Windows Defender ni de Windows Update.**
- **Pas de vidage du Prefetch** : il accélère le lancement des programmes.

## Installation

1. Ouvrez l'onglet **Actions** du dépôt GitHub, choisissez la dernière exécution réussie de **Build** et téléchargez l'artefact **PureClean-win-x64-buildN** (N = numéro de la compilation).
2. Décompressez et lancez `PureClean.exe`. Il est autonome : rien à installer.
3. Windows demande les droits administrateur (nécessaires pour nettoyer `C:\Windows` et modifier les réglages système).

L'exécutable n'est pas signé : SmartScreen peut afficher « Windows a protégé votre ordinateur ». Cliquez sur *Informations complémentaires* puis *Exécuter quand même*.

Les préférences et l'historique sont stockés dans `%LOCALAPPDATA%\PureClean`.

## Compiler soi-même

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) sous Windows (ou Visual Studio 2022).

```bash
dotnet build PureClean.sln -c Release
```

Exécutable autonome en un seul fichier :

```bash
dotnet publish src/PureClean/PureClean.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Vérification des erreurs XAML invisibles à la compilation (ressources manquantes, liaisons impossibles) :

```bash
python tools/check_xaml.py
```

## Architecture

```
src/PureClean/
├── Core/
│   ├── CleanerCatalog.cs       Tout ce qui est nettoyable
│   ├── PathCleaner.cs          Nettoyeur générique basé sur des dossiers
│   ├── AutoClean.cs            Nettoyage planifié sans fenêtre
│   ├── Cleaners/               Corbeille, DNS, nettoyages avancés
│   ├── DiskAnalysis/           Analyse de l'espace disque
│   ├── Duplicates/             Recherche de doublons
│   ├── Programs/               Programmes installés
│   ├── Software/               Catalogue, profils, installation de RustDesk
│   ├── Updates/                Client winget
│   ├── Startup/                Programmes et tâches au démarrage
│   └── Tweaks/                 Réglages Windows réversibles
├── Infrastructure/             Fichiers, corbeille, paramètres, thème, P/Invoke, MVVM
├── Resources/                  Styles, thèmes clair/sombre, icône
├── ViewModels/                 Logique de l'interface
└── Views/                      Pages WPF
```

### Sécurité
- Les liens symboliques et jonctions ne sont **jamais suivis** : impossible de sortir du dossier ciblé.
- Les fichiers verrouillés sont ignorés sans erreur, et les fichiers temporaires récents (moins de 24 h) sont conservés.
- Seul le contenu des dossiers ciblés est supprimé, jamais les dossiers eux-mêmes.
- Les suppressions faites depuis l'analyse disque et les doublons passent par la corbeille. Windows prévient si un élément est trop gros pour y aller.

### Ajouter un élément à nettoyer
Ajoutez une entrée dans `Core/CleanerCatalog.cs` :

```csharp
yield return new PathCleaner(
    "Mon application — cache",
    "Description affichée dans l'interface.",
    CleanerCategory.Applications,
    () => [new CleanTarget(Path.Combine(LocalAppData, "MonApp", "Cache"))],
    blockingProcesses: ["monapp"]);
```

### Ajouter un réglage
Ajoutez une entrée dans `Core/Tweaks/TweakCatalog.cs` en indiquant, pour chaque valeur de registre, la valeur « optimisée » et la valeur Windows d'origine (`null` = supprimer la valeur).

## Licence

MIT — voir [LICENSE](LICENSE).
