import { createRequire } from 'node:module';
import { afterEach, describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);
const { createServerAuthControl, normalizeRendererResponse } = require('../../../src-electron/serverAuthControl.js');

let control;

function response(overrides = {}) {
    return {
        ok: true,
        status: 'requires_authentication',
        currentUserId: null,
        challenge: null,
        errorCode: null,
        ...overrides
    };
}

function worldDocument(overrides = {}) {
    return {
        type: 'world',
        id: 'wrld_example',
        name: 'Example World',
        ownerId: 'usr_author',
        description: 'A world',
        updatedAt: '2026-11-06T00:00:00Z',
        payload: {
            id: 'wrld_example',
            name: 'Example World',
            authorId: 'usr_author'
        },
        ...overrides
    };
}

async function start({ ready = true, dispatch = vi.fn(() => response()), getUnavailableErrorCode = () => null } = {}) {
    control = createServerAuthControl({
        port: 0,
        isRendererReady: () => ready,
        getUnavailableErrorCode,
        dispatch
    });
    await control.listen();
    const { port } = control.address();
    return { baseUrl: 'http://127.0.0.1:' + port, dispatch };
}

afterEach(async () => {
    if (control) {
        await control.close();
        control = null;
    }
});

describe('server auth control', () => {
    it('exposes only the fixed status operation', async () => {
        const { baseUrl, dispatch } = await start();

        const result = await fetch(baseUrl + '/control/v1/status');
        expect(result.status).toBe(200);
        expect(await result.json()).toEqual(response());
        expect(dispatch).toHaveBeenCalledWith({ operation: 'status', payload: {} });

        const unknown = await fetch(baseUrl + '/control/v1/custom');
        expect(unknown.status).toBe(404);
        expect(await unknown.json()).toEqual(response({ ok: false, errorCode: 'not_found' }));
    });

    it('rejects generic URL or header fields in login payloads', async () => {
        const { baseUrl, dispatch } = await start();
        const result = await fetch(baseUrl + '/control/v1/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                username: 'test@example.com',
                password: 'not-logged',
                endpoint: 'https://example.invalid',
                headers: { Authorization: 'forbidden' }
            })
        });

        expect(result.status).toBe(400);
        expect(await result.json()).toEqual(response({ ok: false, errorCode: 'invalid_request' }));
        expect(dispatch).not.toHaveBeenCalled();
    });

    it('passes only the typed two-factor kind and code to the renderer', async () => {
        const challenge = response({
            status: 'two_factor_required',
            challenge: { kinds: ['totp', 'otp'] }
        });
        const { baseUrl, dispatch } = await start({ dispatch: vi.fn(() => challenge) });
        const result = await fetch(baseUrl + '/control/v1/two-factor', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ kind: 'totp', code: '123456' })
        });

        expect(result.status).toBe(200);
        expect(await result.json()).toEqual(challenge);
        expect(dispatch).toHaveBeenCalledWith({
            operation: 'two-factor',
            payload: { kind: 'totp', code: '123456' }
        });
    });

    it('fails closed while the renderer is unavailable', async () => {
        const { baseUrl, dispatch } = await start({ ready: false });
        const result = await fetch(baseUrl + '/control/v1/status');

        expect(result.status).toBe(503);
        expect(await result.json()).toEqual(response({ ok: false, errorCode: 'renderer_unavailable' }));
        expect(dispatch).not.toHaveBeenCalled();
    });

    it.each([
        ['database_initialization_failed', 'database_initialization_failed'],
        ['sensitive database path and exception', 'renderer_unavailable']
    ])('reports only the fixed startup failure code (%s)', async (input, expected) => {
        const { baseUrl, dispatch } = await start({ ready: false, getUnavailableErrorCode: () => input });
        const result = await fetch(baseUrl + '/control/v1/status');
        expect(result.status).toBe(503);
        expect(await result.json()).toEqual(response({ ok: false, errorCode: expected }));
        expect(dispatch).not.toHaveBeenCalled();
    });

    it('rejects renderer responses containing cookies or other unknown fields', async () => {
        expect(() => normalizeRendererResponse({ ...response(), cookie: 'secret' })).toThrow();

        const { baseUrl } = await start({
            dispatch: vi.fn(() => ({ ...response(), token: 'secret' }))
        });
        const result = await fetch(baseUrl + '/control/v1/status');
        const body = await result.json();

        expect(result.status).toBe(500);
        expect(body).toEqual(response({ ok: false, errorCode: 'internal_error' }));
        expect(JSON.stringify(body)).not.toContain('secret');
    });

    it('forwards only bounded typed search input and validates fixed entity documents', async () => {
        const searchResponse = {
            ok: true,
            items: [worldDocument()],
            total: 1,
            offset: 0,
            limit: 60,
            errorCode: null
        };
        const { baseUrl, dispatch } = await start({ dispatch: vi.fn(() => searchResponse) });
        const result = await fetch(baseUrl + '/control/v1/search', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                type: 'world',
                query: ' example ',
                offset: 0,
                limit: 60,
                options: {
                    sort: 'relevance',
                    order: 'descending',
                    featured: false,
                    tag: 'system_approved'
                }
            })
        });

        expect(result.status).toBe(200);
        expect(await result.json()).toEqual(searchResponse);
        expect(dispatch).toHaveBeenCalledWith({
            operation: 'search',
            payload: {
                type: 'world',
                query: 'example',
                offset: 0,
                limit: 60,
                options: {
                    sort: 'relevance',
                    order: 'descending',
                    featured: false,
                    tag: 'system_approved'
                }
            }
        });

        const category = await fetch(baseUrl + '/control/v1/search', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                type: 'world',
                query: '',
                offset: 0,
                limit: 60,
                options: { worldOption: 'favorites', order: 'descending', tag: 'system_approved' }
            })
        });
        expect(category.status).toBe(200);
        expect(dispatch).toHaveBeenLastCalledWith({
            operation: 'search',
            payload: {
                type: 'world',
                query: '',
                offset: 0,
                limit: 60,
                options: { worldOption: 'favorites', order: 'descending', tag: 'system_approved' }
            }
        });

        const arbitrary = await fetch(baseUrl + '/control/v1/search', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                type: 'world',
                query: 'example',
                offset: 0,
                limit: 60,
                options: {},
                url: 'https://example.invalid',
                headers: { Authorization: 'forbidden' }
            })
        });
        expect(arbitrary.status).toBe(400);

        const arbitraryOption = await fetch(baseUrl + '/control/v1/search', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                type: 'world',
                query: '',
                offset: 0,
                limit: 60,
                options: { endpoint: 'https://example.invalid' }
            })
        });
        expect(arbitraryOption.status).toBe(400);
    });

    it('rejects mismatched or unknown fields in renderer read responses', async () => {
        const { baseUrl } = await start({
            dispatch: vi.fn(() => ({
                ok: true,
                item: worldDocument({ payload: { id: 'wrld_example', cookie: 'secret' } }),
                errorCode: null
            }))
        });
        const result = await fetch(baseUrl + '/control/v1/entity', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ type: 'world', id: 'wrld_example' })
        });
        const body = await result.json();

        expect(result.status).toBe(500);
        expect(body.errorCode).toBe('internal_error');
        expect(JSON.stringify(body)).not.toContain('secret');
    });

    it('forwards only fixed typed database reads and validates instance activity rows', async () => {
        const databaseResponse = {
            ok: true,
            result: {
                currentUserData: [
                    {
                        id: 7,
                        created_at: '2026-11-06T12:00:00Z',
                        type: 'OnPlayerLeft',
                        display_name: 'Current User',
                        location: 'wrld_example:123',
                        user_id: 'usr_current',
                        time: 60_000
                    }
                ],
                detailData: []
            },
            errorCode: null
        };
        const { baseUrl, dispatch } = await start({ dispatch: vi.fn(() => databaseResponse) });
        const result = await fetch(baseUrl + '/control/v1/database-read', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name: 'getInstanceActivity',
                arguments: {
                    startDate: '2026-11-06T00:00:00Z',
                    endDate: '2026-11-06T23:59:59Z'
                }
            })
        });

        expect(result.status).toBe(200);
        expect(await result.json()).toEqual(databaseResponse);
        expect(dispatch).toHaveBeenCalledWith({
            operation: 'database-read',
            payload: {
                name: 'getInstanceActivity',
                arguments: {
                    startDate: '2026-11-06T00:00:00Z',
                    endDate: '2026-11-06T23:59:59Z'
                }
            }
        });
    });

    it('rejects arbitrary database methods, arguments, and unknown result fields', async () => {
        const { baseUrl, dispatch } = await start();
        for (const body of [
            { name: 'executeSql', arguments: { sql: 'SELECT * FROM cookies' } },
            { name: 'getHotWorlds', arguments: { days: 30, limit: 30, url: 'https://example.invalid' } },
            { name: 'getHotWorldFriendDetail', arguments: { worldId: 'usr_wrong', days: 30 } },
            {
                name: 'getInstanceActivity',
                arguments: { startDate: '2026-01-01T00:00:00Z', endDate: '2026-11-06T00:00:00Z' }
            }
        ]) {
            const result = await fetch(baseUrl + '/control/v1/database-read', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            expect(result.status).toBe(400);
        }
        expect(dispatch).not.toHaveBeenCalled();

        await control.close();
        control = null;
        const invalid = await start({
            dispatch: vi.fn(() => ({
                ok: true,
                result: [{ friendId: 'usr_friend', mutualIds: ['usr_mutual'], cookie: 'secret' }],
                errorCode: null
            }))
        });
        const invalidResult = await fetch(invalid.baseUrl + '/control/v1/database-read', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: 'getMutualGraphSnapshot', arguments: {} })
        });
        const responseBody = await invalidResult.json();
        expect(invalidResult.status).toBe(500);
        expect(responseBody.errorCode).toBe('internal_error');
        expect(JSON.stringify(responseBody)).not.toContain('secret');
    });

    it('forwards only exact entity resources and rejects recursive secrets in their response', async () => {
        const response = {
            ok: true,
            result: [{ id: 'grp_example', name: 'Example Group', memberCount: 12 }],
            errorCode: null
        };
        const { baseUrl, dispatch } = await start({ dispatch: vi.fn(() => response) });
        const result = await fetch(baseUrl + '/control/v1/entity-resource', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: 'userGroups', arguments: { userId: 'usr_target' } })
        });

        expect(result.status).toBe(200);
        expect(await result.json()).toEqual(response);
        expect(dispatch).toHaveBeenCalledWith({
            operation: 'entity-resource',
            payload: { name: 'userGroups', arguments: { userId: 'usr_target' } }
        });

        await control.close();
        control = null;
        const invalid = await start({
            dispatch: vi.fn(() => ({
                ok: true,
                result: [{ id: 'grp_example', nested: { sessionToken: 'must-not-cross' } }],
                errorCode: null
            }))
        });
        const invalidResult = await fetch(invalid.baseUrl + '/control/v1/entity-resource', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: 'userGroups', arguments: { userId: 'usr_target' } })
        });
        expect(invalidResult.status).toBe(500);
        expect(await invalidResult.json()).toMatchObject({ errorCode: 'internal_error' });
    });
});
