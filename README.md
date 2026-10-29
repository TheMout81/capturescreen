# PureClean

Nettoyeur et optimiseur pour Windows 10 / 11, dans l'esprit de CCleaner, **sans publicité, sans abonnement, sans télémétrie et sans aucune connexion réseau**.

- Aucune dépendance externe : uniquement .NET 8 et les API de Windows.
- Aucune requête réseau : l'application ne contacte aucun serveur, jamais.
- Pas de « version Pro », pas de surveillance en arrière-plan, pas de mise à jour forcée.

## Fonctionnalités

### Nettoyage
L'analyse ne supprime rien : elle indique l'espace récupérable par élément. Le nettoyage demande confirmation.

| Windows | Applications et navigateurs |
|---|---|
| Fichiers temporaires utilisateur et système (plus de 24 h) | Chrome, Edge, Brave, Vivaldi, Opera : caches web, code et GPU de tous les profils |
| Corbeille | Firefox : cache de tous les profils |
| Cache de Windows Update | Discord, Visual Studio Code, Teams (classique) |
| Cache d'optimisation de distribution | Spotify (cache de lecture), Steam (cache web) |
| Rapports d'erreurs (WER), vidages mémoire | |
| Anciens journaux CBS / DISM | |
| Cache des miniatures, documents récents, cache DNS *(décochés par défaut)* | |

Seules les applications réellement installées sont affichées. Si un programme concerné est ouvert, PureClean le signale avant de nettoyer.

### Démarrage
Liste les programmes lancés au démarrage (registre, dossiers Démarrage) et les tâches planifiées tierces, avec un interrupteur pour chacun.
La désactivation utilise le même mécanisme que le Gestionnaire des tâches (clés `StartupApproved`) : rien n'est supprimé, tout est réversible. Les tâches système de Microsoft ne sont pas affichées.

### Réglages Windows
Optimisations de confidentialité (télémétrie, identifiant publicitaire, publicités du menu Démarrer et de l'écran de verrouillage, recherche Bing, historique d'activités), de réactivité (délai au démarrage, animations, transparence, Game DVR, applications en arrière-plan) et de l'Explorateur (extensions, fichiers cachés).
Chaque réglage mémorise la valeur Windows d'origine et peut être annulé. Un point de restauration système est proposé avant toute modification.

## Ce que PureClean ne fait volontairement pas

- **Pas de « nettoyeur de registre »** : le gain est nul et le risque de casser Windows réel.
- **Pas de suppression des cookies, mots de passe, historiques ou sessions** : vous resteriez connecté nulle part.
- **Pas de désactivation de Windows Defender ni de Windows Update.**
- **Pas de vidage du Prefetch** : il accélère le lancement des programmes ; le vider ralentit le PC.

## Installation

1. Ouvrez l'onglet **Actions** du dépôt GitHub, choisissez la dernière exécution réussie de **Build** et téléchargez l'artefact **PureClean-win-x64** (ou la page **Releases** si une version a été publiée).
2. Décompressez et lancez `PureClean.exe`. Il est autonome : rien à installer.
3. Windows demande les droits administrateur (nécessaires pour nettoyer `C:\Windows` et modifier les réglages système).

L'exécutable n'est pas signé : SmartScreen peut afficher « Windows a protégé votre ordinateur ». Cliquez sur *Informations complémentaires* puis *Exécuter quand même*.

## Compiler soi-même

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) sous Windows (ou Visual Studio 2022).

```bash
dotnet build PureClean.sln -c Release
```

Exécutable autonome en un seul fichier :

```bash
dotnet publish src/PureClean/PureClean.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Architecture

```
src/PureClean/
├── Core/
│   ├── CleanerCatalog.cs       Liste de tout ce qui est nettoyable
│   ├── PathCleaner.cs          Nettoyeur générique basé sur des dossiers
│   ├── Cleaners/               Cas particuliers (corbeille, DNS)
│   ├── Startup/                Programmes et tâches au démarrage
│   └── Tweaks/                 Réglages Windows réversibles
├── Infrastructure/             Accès fichiers sécurisé, P/Invoke, MVVM
├── ViewModels/                 Logique de l'interface
└── Views/                      Interface WPF
```

### Sécurité du nettoyage
- Les liens symboliques et jonctions ne sont **jamais suivis** : impossible de sortir du dossier ciblé.
- Les fichiers verrouillés sont ignorés sans erreur ; les fichiers temporaires récents (moins de 24 h) sont conservés.
- Seul le contenu des dossiers ciblés est supprimé, jamais les dossiers eux-mêmes.

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
