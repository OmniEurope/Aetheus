// SPDX-License-Identifier: EUPL-1.2
// The fingerprint of a database schema state: the ordered list of applied EF migration ids.
//
// Two sources produce the same list, which is what makes the seal verifiable rather than declarative:
//   - the source tree, where every migration is a file named exactly after its MigrationId;
//   - a live database, via SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId".
//
// QA compares the two and fails on any divergence, so a contract cannot claim a schema the code does
// not actually produce.
import { createHash } from "node:crypto";
import { readdir } from "node:fs/promises";

/** Hashes a canonical migration list. The plain list travels with it for debugging. */
export const fingerprint = migrations => ({
  hash: createHash("sha256").update(migrations.join("\n")).digest("hex"),
  migrations
});

/**
 * Reads the migration ids from a source tree.
 *
 * EF writes each migration as `<MigrationId>.cs` next to a `<MigrationId>.Designer.cs`; the designer
 * files and the model snapshot are not migrations and must not enter the list, or the fingerprint
 * would never match a live database.
 */
export const migrationsFromSource = async directory => {
  const entries = await readdir(directory);
  return entries
    .filter(name => name.endsWith(".cs"))
    .filter(name => !name.endsWith(".Designer.cs"))
    .filter(name => !name.endsWith("ModelSnapshot.cs"))
    .map(name => name.slice(0, -".cs".length))
    .sort();
};

/** Parses the raw psql output of the migration-history query. */
export const migrationsFromHistory = text =>
  text.split("\n").map(line => line.trim()).filter(line => line.length > 0).sort();
