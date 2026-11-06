import { afterEach, describe, expect, it, vi } from 'vitest';

import { WebApiClient, itemsFrom } from './api';

function jsonResponse(body, status = 200) {
    return {
        ok: status >= 200 && status < 300,
        status,
        statusText: '',
        headers: { get: () => 'application/json' },
        json: async () => body,
        text: async () => JSON.stringify(body)
    };
}

afterEach(() => vi.unstubAllGlobals());

describe('WebApiClient', () => {
    it.each([
        ['120', 120_000],
        ['Fri, 06 Nov 2026 10:02:00 GMT', 120_000],
        ['invalid', null]
    ])(
        'retains only a parsed Retry-After delay from a rate-limited response: %s',
        async (retryAfter, expectedDelay) => {
            const clock = vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-11-06T10:00:00Z'));
            vi.stubGlobal(
                'fetch',
                vi.fn().mockResolvedValue({
                    ...jsonResponse({ error: 'rate_limited' }, 429),
                    headers: {
                        get: (name) =>
                            name === 'content-type' ? 'application/json' : name === 'retry-after' ? retryAfter : null
                    }
                })
            );
            try {
                await expect(new WebApiClient().getVrchatAuthStatus()).rejects.toMatchObject({
                    status: 429,
                    retryAfterMs: expectedDelay,
                    body: { error: 'rate_limited' }
                });
            } finally {
                clock.mockRestore();
            }
        }
    );

    it('sends named read requests with browser session credentials', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [] }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient('/api/v1/');

        await api.getFriends();

        expect(fetchMock).toHaveBeenCalledWith('/api/v1/friends', expect.objectContaining({ credentials: 'include' }));
        const [, options] = fetchMock.mock.calls[0];
        expect(options.headers.get('Content-Type')).toBeNull();
    });

    it('maps only allowed search resource types to fixed endpoints', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [] }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.search('world', 'test world');
        expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/search?type=world&q=test+world');
        await api.search(
            'world',
            '',
            { offset: 60, limit: 60 },
            {
                worldOption: 'favorites',
                sort: 'popularity',
                order: 'descending',
                featured: false,
                tag: 'author_tag_featured,system_approved',
                user: 'me',
                releaseStatus: 'all'
            }
        );
        expect(fetchMock.mock.calls[1][0]).toBe(
            '/api/v1/search?type=world&q=&offset=60&limit=60&worldOption=favorites&sort=popularity&order=descending&featured=false&user=me&releaseStatus=all&tag=author_tag_featured%2Csystem_approved'
        );
        await api.getSearchDetail('world', 'wrld/test');
        expect(fetchMock.mock.calls[2][0]).toBe('/api/v1/worlds/wrld%2Ftest');
        expect(() => api.search('users', 'test')).toThrow('Unsupported search type.');
        expect(() => api.search('world', '', undefined, { url: 'https://example.invalid' })).toThrow(
            'Unsupported search option.'
        );
    });

    it('uses only the fixed VRChat auth endpoints and accepted fields', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.getVrchatAuthStatus();
        await api.loginVrchat('name', 'password');
        await api.submitVrchatTwoFactor('totp', '123456');
        await api.resendVrchatEmailOtp();
        await api.logoutVrchat();

        expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
            '/api/v1/vrchat/auth/status',
            '/api/v1/vrchat/auth/login',
            '/api/v1/vrchat/auth/two-factor',
            '/api/v1/vrchat/auth/email-otp/resend',
            '/api/v1/vrchat/auth/logout'
        ]);
        expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({
            username: 'name',
            password: 'password'
        });
        expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({
            kind: 'totp',
            code: '123456'
        });
    });

    it('sends the separate host setup token only to the bootstrap endpoint', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.bootstrap('administrator-password', 'host-setup-token');

        const [url, options] = fetchMock.mock.calls[0];
        expect(url).toBe('/api/v1/auth/bootstrap');
        expect(JSON.parse(options.body)).toEqual({
            password: 'administrator-password',
            setupToken: 'host-setup-token'
        });
    });

    it('sends a host-issued recovery code only to the fixed recovery endpoint', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.recoverAdmin('host-issued-code', 'new-administrator-password', 'Recovered browser');

        const [url, options] = fetchMock.mock.calls[0];
        expect(url).toBe('/api/v1/auth/recovery/redeem');
        expect(JSON.parse(options.body)).toEqual({
            code: 'host-issued-code',
            password: 'new-administrator-password',
            deviceName: 'Recovered browser'
        });
    });

    it('uploads snapshots with only the fixed database and SQLite companion fields', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ importedTableCount: 1, importedRowCount: 2 }));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();
        const database = new File(['database'], 'VRCX.sqlite3');
        const wal = new File(['wal'], 'VRCX.sqlite3-wal');
        const shm = new File(['shm'], 'VRCX.sqlite3-shm');

        await api.importSnapshot(database, wal, shm);

        const [url, options] = fetchMock.mock.calls[0];
        expect(url).toBe('/api/v1/setup/import');
        expect(options.body).toBeInstanceOf(FormData);
        expect([...options.body.keys()]).toEqual(['database', 'wal', 'shm']);
        expect(options.headers.get('Content-Type')).toBeNull();
    });

    it('uses only fixed bounded analytics paths and query parameters', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.getInstanceActivityDates();
        await api.getInstanceActivity('2026-11-06T00:00:00Z', '2026-11-06T23:59:59Z');
        await api.getHotWorlds(30, 20);
        await api.getHotWorldFriendDetail('wrld_example', 30);
        await api.getMutualGraphSnapshot();
        await api.getMutualGraphMeta();

        expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
            '/api/v1/analytics/instance-activity/dates',
            '/api/v1/analytics/instance-activity?startDate=2026-11-06T00%3A00%3A00Z&endDate=2026-11-06T23%3A59%3A59Z',
            '/api/v1/analytics/hot-worlds?days=30&limit=20',
            '/api/v1/analytics/hot-worlds/wrld_example/friends?days=30',
            '/api/v1/analytics/mutual-graph/snapshot',
            '/api/v1/analytics/mutual-graph/meta'
        ]);
        expect(() => api.getHotWorlds(0)).toThrow('Invalid analytics query');
        expect(() => api.getHotWorldFriendDetail('usr_wrong', 30)).toThrow('Invalid world id');
        expect(() => api.getInstanceActivity('2026-01-01T00:00:00Z', '2026-11-06T00:00:00Z')).toThrow(
            'Invalid instance activity date range'
        );
    });

    it('normalizes supported paged response shapes', () => {
        expect(itemsFrom([{ id: 1 }])).toEqual([{ id: 1 }]);
        expect(itemsFrom({ items: [{ id: 2 }] })).toEqual([{ id: 2 }]);
        expect(itemsFrom(null)).toEqual([]);
    });

    it('uses only fixed entity subresource paths and rejects private instances', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
        vi.stubGlobal('fetch', fetchMock);
        const api = new WebApiClient();

        await api.getUserWorlds('usr_target', {
            n: 50,
            offset: 100,
            sort: 'updated',
            order: 'descending',
            releaseStatus: 'public'
        });
        await api.getGroupMembers('grp_example', {
            n: 100,
            offset: 0,
            sort: 'joinedAt:desc',
            roleId: 'grol_reader'
        });
        await api.getAvatarGallery('avtr_example');

        expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
            '/api/v1/users/usr_target/worlds?offset=100&limit=50&sort=updated&order=descending&releaseStatus=public&user=',
            '/api/v1/groups/grp_example/members?offset=0&limit=100&sort=joinedAt%3Adesc&roleId=grol_reader',
            '/api/v1/avatars/avtr_example/gallery'
        ]);
        expect(() => api.getWorldInstance('wrld_example', '12345~private(usr_owner)~nonce(secret)')).toThrow(
            'Invalid public instance id'
        );
    });
});
