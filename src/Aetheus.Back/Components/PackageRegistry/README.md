# Module PackageRegistry

## Responsabilité

Registre de paquets interne (npm et NuGet, ADR-037, et dépôt Maven 2) : publication contrôlée, inspection des archives,
stockage sur disque réconcilié et authentification dédiée.

## Points d’entrée

- API ou consommateur principal : `NpmRegistryController.cs`, `NuGetRegistryController.cs`, `MavenRegistryController.cs` et
  `PackageRegistryAdminController.cs`.
- Enregistrement DI : `PackageRegistryModuleExtensions.cs`.
- `PackageRegistryAuthenticationHandler` : authentification des clients de registre.
- `PackageRegistryUploadGate` / `PackageRegistryPublishGate` / `PackageRegistryPublishFinalizer` :
  contrôle du flux de publication ; `NpmPackageInspector` / `NuGetPackageInspector` inspectent les
  archives, `BoundedReadStream` borne les lectures. `MavenRepositoryPath` valide les chemins Maven,
  `MavenPomInspector` vérifie les POM ; ordre des versions, plages et `maven-metadata.xml` sont dans
  `Components/Shared/Maven` (partagés avec `PackageFeeds`).
- `PackageRegistryStorageReconciler` : réconciliation du stockage disque avec la base.
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.
