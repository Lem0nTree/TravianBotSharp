namespace MainCore.Services.Evasion;

// The app uses EnsureCreated rather than migrations, so create only this additive table.
public static class EvasionStore
{
    public static void EnsureSchema(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "EvasionDispatches" (
                "AccountId" INTEGER NOT NULL, "VillageId" INTEGER NOT NULL,
                "AttackId" TEXT NOT NULL, "Kind" TEXT NOT NULL, "DestinationId" INTEGER NOT NULL,
                "ImpactUtc" TEXT NOT NULL, "State" TEXT NOT NULL, "MovementId" TEXT NULL,
                "SentUtc" TEXT NULL, "ArrivalUtc" TEXT NULL, "Payload" TEXT NOT NULL,
                PRIMARY KEY ("AccountId", "VillageId", "AttackId", "Kind"));
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_EvasionDispatches_AccountId_VillageId_Kind_MovementId"
            ON "EvasionDispatches" ("AccountId", "VillageId", "Kind", "MovementId");
            """);
    }

    public static bool TryPlan(AppDbContext db, EvasionDispatch dispatch)
    {
        if (dispatch.Kind is not ("merchant" or "troop") || string.IsNullOrWhiteSpace(dispatch.AttackId)
            || dispatch.AttackId.Length > 200 || dispatch.AccountId <= 0 || dispatch.VillageId <= 0
            || dispatch.DestinationId <= 0 || dispatch.DestinationId == dispatch.VillageId
            || !DateTimeOffset.TryParse(dispatch.ImpactUtc, out _))
            throw new ArgumentException("Invalid evasion dispatch.");
        EnsureSchema(db);
        using var transaction = db.Database.BeginTransaction();
        var owned = db.Villages.Where(x => x.AccountId == dispatch.AccountId &&
            (x.Id == dispatch.VillageId || x.Id == dispatch.DestinationId)).Select(x => x.Id).ToArray();
        if (!EvasionRules.IsOwnedDestination(dispatch.VillageId, dispatch.DestinationId, owned)) return false;
        var inserted = db.Database.ExecuteSqlInterpolated($"""
            INSERT OR IGNORE INTO "EvasionDispatches"
            ("AccountId", "VillageId", "AttackId", "Kind", "DestinationId", "ImpactUtc", "State", "Payload")
            VALUES ({dispatch.AccountId}, {dispatch.VillageId}, {dispatch.AttackId}, {dispatch.Kind},
                    {dispatch.DestinationId}, {dispatch.ImpactUtc}, {'P' + "lanned"}, {dispatch.Payload});
            """);
        transaction.Commit();
        return inserted == 1;
    }

    // Compare-and-swap ensures a restart or concurrent scan cannot confirm the same send twice.
    public static bool BeginSubmit(AppDbContext db, EvasionDispatch key)
    {
        EnsureSchema(db);
        var attemptedUtc = DateTimeOffset.UtcNow.ToString("O");
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE "EvasionDispatches" SET "State" = {'S' + "ubmitting"}, "SentUtc" = {attemptedUtc}
            WHERE "AccountId" = {key.AccountId} AND "VillageId" = {key.VillageId}
              AND "AttackId" = {key.AttackId} AND "Kind" = {key.Kind} AND "State" = {'P' + "lanned"};
            """) == 1;
    }

    public static bool SavePayload(AppDbContext db, EvasionDispatch key, string payload)
    {
        EnsureSchema(db);
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE "EvasionDispatches" SET "Payload" = {payload}
            WHERE "AccountId" = {key.AccountId} AND "VillageId" = {key.VillageId}
              AND "AttackId" = {key.AttackId} AND "Kind" = {key.Kind} AND "State" = {"Planned"};
            """) == 1;
    }

    public static bool RecordSent(AppDbContext db, EvasionDispatch key, string movementId, DateTimeOffset sentAt, DateTimeOffset arrivalAt)
    {
        if (string.IsNullOrWhiteSpace(movementId) || movementId.Length > 200 || arrivalAt <= sentAt)
            throw new ArgumentException("An exact movement identity and valid journey are required.");
        EnsureSchema(db);
        var sent = sentAt.ToUniversalTime().ToString("O");
        var arrival = arrivalAt.ToUniversalTime().ToString("O");
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE "EvasionDispatches" SET "State" = {'S' + "ent"}, "MovementId" = {movementId},
                "SentUtc" = {sent}, "ArrivalUtc" = {arrival}
            WHERE "AccountId" = {key.AccountId} AND "VillageId" = {key.VillageId}
              AND "AttackId" = {key.AttackId} AND "Kind" = {key.Kind} AND "State" = {'S' + "ubmitting"};
            """) == 1;
    }

    public static bool BeginCancel(AppDbContext db, EvasionDispatch key, string movementId)
    {
        if (string.IsNullOrWhiteSpace(movementId)) return false;
        EnsureSchema(db);
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE "EvasionDispatches" SET "State" = {'C' + "ancelling"}
            WHERE "AccountId" = {key.AccountId} AND "VillageId" = {key.VillageId}
              AND "AttackId" = {key.AttackId} AND "Kind" = {'t' + "roop"}
              AND "State" = {'S' + "ent"} AND "MovementId" = {movementId};
            """) == 1;
    }

    public static bool RecordCancelled(AppDbContext db, EvasionDispatch key, string movementId)
    {
        EnsureSchema(db);
        return db.Database.ExecuteSqlInterpolated($"""
            UPDATE "EvasionDispatches" SET "State" = {'C' + "ancelled"}
            WHERE "AccountId" = {key.AccountId} AND "VillageId" = {key.VillageId}
              AND "AttackId" = {key.AttackId} AND "Kind" = {'t' + "roop"}
              AND "State" = {'C' + "ancelling"} AND "MovementId" = {movementId};
            """) == 1;
    }
}
