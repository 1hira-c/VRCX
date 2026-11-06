import sqliteService from './sqlite';
import { runtimeCapabilities } from './runtimeCapabilities';
import { watchState } from './watchState';
import { syncTelemetry } from './syncTelemetry';
import { wsState } from './websocket';
import { buildServerProjectionSnapshot } from './serverProjection';

import * as workerTimers from 'worker-timers';

const PUBLISH_INTERVAL_MS = 10_000;
const OUTBOX_LIMIT = 5_000;

const schemaOperations = [
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_projection_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            current_user_id TEXT NOT NULL DEFAULT '',
            sync_state TEXT NOT NULL,
            last_success_at TEXT,
            last_attempt_at TEXT,
            next_retry_at TEXT,
            error_code TEXT,
            pipeline_state TEXT NOT NULL,
            pipeline_connected_at TEXT,
            pipeline_last_event_at TEXT,
            pipeline_last_attempt_at TEXT,
            pipeline_next_retry_at TEXT,
            pipeline_error_code TEXT
        )`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_user_snapshot (
            user_id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            state TEXT NOT NULL,
            is_friend INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            payload_json TEXT NOT NULL
        )`
    },
    {
        sql: `CREATE INDEX IF NOT EXISTS vrcx_web_user_snapshot_friend_name_idx
            ON vrcx_web_user_snapshot (is_friend, display_name COLLATE NOCASE, user_id)`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_entity_snapshot (
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            owner_id TEXT,
            description TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            PRIMARY KEY (entity_type, entity_id)
        )`
    },
    {
        sql: `CREATE INDEX IF NOT EXISTS vrcx_web_entity_snapshot_name_idx
            ON vrcx_web_entity_snapshot (entity_type, display_name COLLATE NOCASE, entity_id)`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_favorite_projection_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            ready INTEGER NOT NULL DEFAULT 0,
            current_user_id TEXT NOT NULL DEFAULT '',
            updated_at TEXT
        )`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_favorite_snapshot (
            id TEXT PRIMARY KEY,
            type TEXT NOT NULL CHECK (type IN ('friend', 'world', 'vrcPlusWorld', 'avatar')),
            favorite_id TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            group_key TEXT NOT NULL,
            sort_order INTEGER NOT NULL,
            target_name TEXT NOT NULL,
            target_payload_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )`
    },
    {
        sql: `CREATE INDEX IF NOT EXISTS vrcx_web_favorite_snapshot_order_idx
            ON vrcx_web_favorite_snapshot (sort_order, id)`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_favorite_group_snapshot (
            group_key TEXT PRIMARY KEY,
            type TEXT NOT NULL CHECK (type IN ('friend', 'world', 'vrcPlusWorld', 'avatar')),
            name TEXT NOT NULL,
            display_name TEXT NOT NULL,
            capacity INTEGER NOT NULL,
            count INTEGER NOT NULL,
            visibility TEXT NOT NULL,
            sort_order INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        )`
    },
    {
        sql: `CREATE INDEX IF NOT EXISTS vrcx_web_favorite_group_snapshot_order_idx
            ON vrcx_web_favorite_group_snapshot (sort_order, group_key)`
    },
    {
        sql: `CREATE TABLE IF NOT EXISTS vrcx_web_event_outbox (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at TEXT NOT NULL,
            type TEXT NOT NULL CHECK (type IN ('status', 'pipeline', 'data')),
            resource TEXT NOT NULL,
            payload_json TEXT NOT NULL
        )`
    },
    {
        sql: `INSERT OR IGNORE INTO vrcx_web_projection_state (
            id, current_user_id, sync_state, pipeline_state
        ) VALUES (1, '', 'starting', 'disconnected')`
    },
    {
        sql: `INSERT OR IGNORE INTO vrcx_web_favorite_projection_state (
            id, ready, current_user_id
        ) VALUES (1, 0, '')`
    }
];

function getSyncView(stores) {
    const authenticating = Boolean(stores.auth.loginForm?.loading || stores.auth.attemptingAutoLogin);
    let state = syncTelemetry.state;
    if (authenticating) {
        state = 'authenticating';
    } else if (!watchState.isLoggedIn) {
        state = 'requires_authentication';
    } else if (stores.friend.isRefreshFriendsLoading || !watchState.isFriendsLoaded) {
        state = 'syncing';
    } else if (state === 'starting') {
        state = 'healthy';
    }
    return {
        state,
        lastSuccessAt: syncTelemetry.lastSuccessAt,
        lastAttemptAt: syncTelemetry.lastAttemptAt,
        nextRetryAt: syncTelemetry.nextRetryAt,
        errorCode: syncTelemetry.errorCode
    };
}

function getPipelineView() {
    let state = 'disconnected';
    if (wsState.connected) {
        state = 'connected';
    } else if (wsState.connecting) {
        state = 'connecting';
    } else if (wsState.nextRetryAt) {
        state = 'reconnecting';
    } else if (wsState.errorCode) {
        state = 'error';
    }
    return {
        state,
        connectedAt: wsState.connectedAt,
        lastEventAt: wsState.lastEventAt,
        lastAttemptAt: wsState.lastAttemptAt,
        nextRetryAt: wsState.nextRetryAt,
        errorCode: wsState.errorCode
    };
}

function addOutboxOperation(operations, occurredAt, type, resource, payload) {
    operations.push({
        sql: `INSERT INTO vrcx_web_event_outbox (occurred_at, type, resource, payload_json)
            VALUES (@occurred_at, @type, @resource, @payload_json)`,
        args: {
            '@occurred_at': occurredAt,
            '@type': type,
            '@resource': resource,
            '@payload_json': JSON.stringify(payload)
        }
    });
}

function createStateOperation(currentUserId, sync, pipeline) {
    return {
        sql: `INSERT INTO vrcx_web_projection_state (
                id, current_user_id, sync_state, last_success_at, last_attempt_at,
                next_retry_at, error_code, pipeline_state, pipeline_connected_at,
                pipeline_last_event_at, pipeline_last_attempt_at, pipeline_next_retry_at,
                pipeline_error_code
            ) VALUES (
                1, @current_user_id, @sync_state, @last_success_at, @last_attempt_at,
                @next_retry_at, @error_code, @pipeline_state, @pipeline_connected_at,
                @pipeline_last_event_at, @pipeline_last_attempt_at, @pipeline_next_retry_at,
                @pipeline_error_code
            )
            ON CONFLICT(id) DO UPDATE SET
                current_user_id = excluded.current_user_id,
                sync_state = excluded.sync_state,
                last_success_at = COALESCE(excluded.last_success_at, vrcx_web_projection_state.last_success_at),
                last_attempt_at = excluded.last_attempt_at,
                next_retry_at = excluded.next_retry_at,
                error_code = excluded.error_code,
                pipeline_state = excluded.pipeline_state,
                pipeline_connected_at = COALESCE(
                    excluded.pipeline_connected_at,
                    vrcx_web_projection_state.pipeline_connected_at
                ),
                pipeline_last_event_at = COALESCE(
                    excluded.pipeline_last_event_at,
                    vrcx_web_projection_state.pipeline_last_event_at
                ),
                pipeline_last_attempt_at = excluded.pipeline_last_attempt_at,
                pipeline_next_retry_at = excluded.pipeline_next_retry_at,
                pipeline_error_code = excluded.pipeline_error_code`,
        args: {
            '@current_user_id': currentUserId,
            '@sync_state': sync.state,
            '@last_success_at': sync.lastSuccessAt,
            '@last_attempt_at': sync.lastAttemptAt,
            '@next_retry_at': sync.nextRetryAt,
            '@error_code': sync.errorCode,
            '@pipeline_state': pipeline.state,
            '@pipeline_connected_at': pipeline.connectedAt,
            '@pipeline_last_event_at': pipeline.lastEventAt,
            '@pipeline_last_attempt_at': pipeline.lastAttemptAt,
            '@pipeline_next_retry_at': pipeline.nextRetryAt,
            '@pipeline_error_code': pipeline.errorCode
        }
    };
}

function addSnapshotOperations(operations, snapshot) {
    operations.push({ sql: 'DELETE FROM vrcx_web_user_snapshot' });
    for (const user of snapshot.users) {
        operations.push({
            sql: `INSERT INTO vrcx_web_user_snapshot (
                user_id, display_name, state, is_friend, updated_at, payload_json
            ) VALUES (@user_id, @display_name, @state, @is_friend, @updated_at, @payload_json)`,
            args: {
                '@user_id': user.userId,
                '@display_name': user.displayName,
                '@state': user.state,
                '@is_friend': user.isFriend ? 1 : 0,
                '@updated_at': user.updatedAt,
                '@payload_json': JSON.stringify(user.payload)
            }
        });
    }

    operations.push({ sql: 'DELETE FROM vrcx_web_entity_snapshot' });
    for (const entity of snapshot.entities) {
        operations.push({
            sql: `INSERT INTO vrcx_web_entity_snapshot (
                entity_type, entity_id, display_name, owner_id, description, updated_at, payload_json
            ) VALUES (
                @entity_type, @entity_id, @display_name, @owner_id, @description, @updated_at, @payload_json
            )`,
            args: {
                '@entity_type': entity.entityType,
                '@entity_id': entity.entityId,
                '@display_name': entity.displayName,
                '@owner_id': entity.ownerId,
                '@description': entity.description,
                '@updated_at': entity.updatedAt,
                '@payload_json': JSON.stringify(entity.payload)
            }
        });
    }
}

function addFavoriteAccountGuardOperation(operations, currentUserId, updatedAt) {
    operations.push({
        sql: `UPDATE vrcx_web_favorite_projection_state
            SET ready = 0, updated_at = @updated_at
            WHERE id = 1 AND ready = 1 AND current_user_id <> @current_user_id`,
        args: {
            '@current_user_id': currentUserId,
            '@updated_at': updatedAt
        }
    });
}

function addFavoriteSnapshotOperations(operations, snapshot, updatedAt) {
    operations.push({ sql: 'DELETE FROM vrcx_web_favorite_snapshot' });
    for (const favorite of snapshot.favorites ?? []) {
        operations.push({
            sql: `INSERT INTO vrcx_web_favorite_snapshot (
                id, type, favorite_id, tags_json, group_key, sort_order,
                target_name, target_payload_json, updated_at
            ) VALUES (
                @id, @type, @favorite_id, @tags_json, @group_key, @sort_order,
                @target_name, @target_payload_json, @updated_at
            )`,
            args: {
                '@id': favorite.id,
                '@type': favorite.type,
                '@favorite_id': favorite.favoriteId,
                '@tags_json': JSON.stringify(favorite.tags),
                '@group_key': favorite.groupKey,
                '@sort_order': favorite.sortOrder,
                '@target_name': favorite.targetName,
                '@target_payload_json': JSON.stringify(favorite.targetPayload),
                '@updated_at': updatedAt
            }
        });
    }

    operations.push({ sql: 'DELETE FROM vrcx_web_favorite_group_snapshot' });
    for (const group of snapshot.favoriteGroups ?? []) {
        operations.push({
            sql: `INSERT INTO vrcx_web_favorite_group_snapshot (
                group_key, type, name, display_name, capacity, count,
                visibility, sort_order, updated_at
            ) VALUES (
                @group_key, @type, @name, @display_name, @capacity, @count,
                @visibility, @sort_order, @updated_at
            )`,
            args: {
                '@group_key': group.key,
                '@type': group.type,
                '@name': group.name,
                '@display_name': group.displayName,
                '@capacity': group.capacity,
                '@count': group.count,
                '@visibility': group.visibility,
                '@sort_order': group.sortOrder,
                '@updated_at': updatedAt
            }
        });
    }
    operations.push({
        sql: `INSERT INTO vrcx_web_favorite_projection_state (
                id, ready, current_user_id, updated_at
            ) VALUES (1, 1, @current_user_id, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                ready = 1,
                current_user_id = excluded.current_user_id,
                updated_at = excluded.updated_at`,
        args: {
            '@current_user_id': snapshot.currentUserId,
            '@updated_at': updatedAt
        }
    });
}

let publisherTimer = null;
let publishing = false;
let publishAgain = false;
let lastDataFingerprint = null;
let lastFavoriteFingerprint = null;
let lastStatusSignature = null;
let lastPipelineSignature = null;

async function publish(stores) {
    const now = new Date().toJSON();
    const snapshot = buildServerProjectionSnapshot(stores, now);
    const sync = getSyncView(stores);
    const pipeline = getPipelineView();
    const statusSignature = JSON.stringify({
        currentUserId: snapshot.currentUserId,
        state: sync.state,
        lastSuccessAt: sync.lastSuccessAt,
        lastAttemptAt: sync.lastAttemptAt,
        nextRetryAt: sync.nextRetryAt,
        error: sync.errorCode
    });
    const pipelineSignature = JSON.stringify({
        state: pipeline.state,
        connectedAt: pipeline.connectedAt,
        lastEventAt: pipeline.lastEventAt,
        lastAttemptAt: pipeline.lastAttemptAt,
        nextRetryAt: pipeline.nextRetryAt,
        error: pipeline.errorCode
    });
    const canPublishDomainSnapshot = watchState.isLoggedIn && Boolean(snapshot.currentUserId);
    const dataChanged = canPublishDomainSnapshot && snapshot.fingerprint !== lastDataFingerprint;
    const canPublishFavoriteSnapshot = canPublishDomainSnapshot && watchState.isFavoritesLoaded;
    const favoritesChanged = canPublishFavoriteSnapshot && snapshot.favoriteFingerprint !== lastFavoriteFingerprint;
    /** @type {{ sql: string; args?: Record<string, unknown> }[]} */
    const operations = [createStateOperation(snapshot.currentUserId, sync, pipeline)];

    if (dataChanged) {
        addSnapshotOperations(operations, snapshot);
        addOutboxOperation(operations, now, 'data', 'snapshot', {
            currentUserId: snapshot.currentUserId,
            users: snapshot.users.length,
            entities: snapshot.entities.length
        });
    }
    if (canPublishDomainSnapshot) {
        addFavoriteAccountGuardOperation(operations, snapshot.currentUserId, now);
    }
    if (favoritesChanged) {
        addFavoriteSnapshotOperations(operations, snapshot, now);
        addOutboxOperation(operations, now, 'data', 'favorites', {
            ready: true,
            favorites: snapshot.favorites?.length ?? 0,
            groups: snapshot.favoriteGroups?.length ?? 0
        });
    }
    if (statusSignature !== lastStatusSignature) {
        addOutboxOperation(operations, now, 'status', 'status', {
            currentUserId: snapshot.currentUserId,
            state: sync.state,
            errorCode: sync.errorCode
        });
    }
    if (pipelineSignature !== lastPipelineSignature) {
        addOutboxOperation(operations, now, 'pipeline', 'pipeline', {
            state: pipeline.state,
            connectedAt: pipeline.connectedAt,
            lastEventAt: pipeline.lastEventAt,
            errorCode: pipeline.errorCode
        });
    }
    operations.push({
        sql: `DELETE FROM vrcx_web_event_outbox
            WHERE sequence < (
                SELECT sequence FROM vrcx_web_event_outbox
                ORDER BY sequence DESC LIMIT 1 OFFSET ${OUTBOX_LIMIT - 1}
            )`
    });

    await sqliteService.executeTransaction(operations);
    if (canPublishDomainSnapshot) {
        lastDataFingerprint = snapshot.fingerprint;
    }
    if (canPublishFavoriteSnapshot) {
        lastFavoriteFingerprint = snapshot.favoriteFingerprint;
    }
    lastStatusSignature = statusSignature;
    lastPipelineSignature = pipelineSignature;
}

async function requestPublish(stores) {
    if (publishing) {
        publishAgain = true;
        return;
    }
    publishing = true;
    try {
        do {
            publishAgain = false;
            await publish(stores);
        } while (publishAgain);
    } catch (error) {
        console.error('Server projection publish failed', error);
    } finally {
        publishing = false;
    }
}

/**
 * Starts the server-only read-model publisher after the original VRCX database
 * and stores have initialized.
 *
 * @param {ReturnType<import('../stores').createGlobalStores>} stores
 * @returns {Promise<() => void>}
 */
export async function startServerProjectionPublisher(stores) {
    if (!runtimeCapabilities.serverMode) {
        return () => {};
    }
    if (publisherTimer !== null) {
        return () => {};
    }
    await sqliteService.executeTransaction(schemaOperations);
    await requestPublish(stores);
    publisherTimer = workerTimers.setInterval(() => {
        void requestPublish(stores);
    }, PUBLISH_INTERVAL_MS);
    return () => {
        if (publisherTimer !== null) {
            workerTimers.clearInterval(publisherTimer);
            publisherTimer = null;
        }
    };
}
