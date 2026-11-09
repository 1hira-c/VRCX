import sqliteService from './sqlite.js';

// This is deliberately a fixed, single-row metadata record. It is not a
// browser-provided account selector and is only written after trusted import
// or a completed native VRChat login.
const DATA_OWNER_TABLE = 'vrcx_web_data_owner';
const DATA_OWNER_CREATE_SQL = `
    CREATE TABLE IF NOT EXISTS ${DATA_OWNER_TABLE} (
        id INTEGER PRIMARY KEY CHECK (id = 1),
        user_id TEXT NOT NULL,
        imported_at TEXT NOT NULL
    )
`;
const DATA_OWNER_SELECT_SQL = `SELECT user_id FROM ${DATA_OWNER_TABLE} WHERE id = 1`;
const LEGACY_OWNER_SELECT_SQL = "SELECT value FROM configs WHERE key = 'config:lastuserloggedin'";

function isTrustedUserId(userId) {
    return (
        typeof userId === 'string' &&
        userId.length > 4 &&
        userId.length <= 128 &&
        userId.startsWith('usr_') &&
        /^[A-Za-z0-9_-]+$/.test(userId)
    );
}

async function hasTable(name) {
    let found = false;
    await sqliteService.execute(
        () => {
            found = true;
        },
        "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = @name LIMIT 1",
        { '@name': name }
    );
    return found;
}

async function readSingleValue(sql) {
    let value;
    await sqliteService.execute((row) => {
        value = row[0];
    }, sql);
    return value;
}

/**
 * Persist the account identity proven by the original native login flow.
 * This has no browser/API input path; callers must invoke it only after the
 * renderer has obtained and initialized the authenticated current user.
 */
export async function recordAuthenticatedDataOwner(userId) {
    if (!isTrustedUserId(userId)) {
        return false;
    }

    await sqliteService.executeNonQuery(DATA_OWNER_CREATE_SQL);
    await sqliteService.executeNonQuery(
        `INSERT INTO ${DATA_OWNER_TABLE} (id, user_id, imported_at)
         VALUES (1, @userId, @recordedAt)
         ON CONFLICT(id) DO UPDATE SET
             user_id = excluded.user_id,
             imported_at = excluded.imported_at`,
        {
            '@userId': userId,
            '@recordedAt': new Date().toISOString()
        }
    );
    return true;
}

/**
 * Return the durable data owner. A legacy config value is used only when the
 * dedicated record has not been created yet, then immediately migrated into
 * the dedicated record through the same native SQLite writer.
 */
export async function readServerDataOwner() {
    if (await hasTable(DATA_OWNER_TABLE)) {
        const owner = await readSingleValue(DATA_OWNER_SELECT_SQL);
        // A present malformed row is not a migration trigger: do not replace
        // a dedicated record using a weaker legacy source.
        if (owner !== undefined) {
            return isTrustedUserId(owner) ? owner : null;
        }
    }

    if (!(await hasTable('configs'))) {
        return null;
    }
    const legacyOwner = await readSingleValue(LEGACY_OWNER_SELECT_SQL);
    if (!isTrustedUserId(legacyOwner)) {
        return null;
    }

    await recordAuthenticatedDataOwner(legacyOwner);
    return legacyOwner;
}
