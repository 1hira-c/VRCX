import { beforeEach, describe, expect, it, vi } from 'vitest';

const webApi = vi.hoisted(() => ({
    getInstanceActivityDates: vi.fn(),
    getInstanceActivity: vi.fn(),
    getHotWorlds: vi.fn(),
    getHotWorldFriendDetail: vi.fn(),
    getMutualGraphSnapshot: vi.fn(),
    getMutualGraphMeta: vi.fn()
}));

vi.mock('./api', () => ({ webApi }));

import { webDatabase } from './webDatabase';

describe('web analytics database adapter', () => {
    beforeEach(() => {
        for (const mock of Object.values(webApi)) mock.mockReset();
    });

    it('restores the original instance activity result including its detail Map', async () => {
        const row = {
            id: 1,
            created_at: '2026-11-06T12:00:00Z',
            type: 'OnPlayerLeft',
            display_name: 'Current User',
            location: 'wrld_example:123',
            user_id: 'usr_current',
            time: 60_000
        };
        webApi.getInstanceActivityDates.mockResolvedValue(['2026-11-06T12:00:00Z']);
        webApi.getInstanceActivity.mockResolvedValue({
            currentUserData: [row],
            detailData: [{ location: 'wrld_example:123', rows: [row] }]
        });

        await expect(webDatabase.getDateOfInstanceActivity()).resolves.toEqual(['2026-11-06T12:00:00Z']);
        const result = await webDatabase.getInstanceActivity('2026-11-06T00:00:00Z', '2026-11-06T23:59:59Z');

        expect(webApi.getInstanceActivity).toHaveBeenCalledWith('2026-11-06T00:00:00Z', '2026-11-06T23:59:59Z');
        expect(result.currentUserData).toEqual([row]);
        expect(result.detailData).toBeInstanceOf(Map);
        expect(result.detailData.get('wrld_example:123')).toEqual([row]);
    });

    it('preserves the original hot-world arrays and reconstructs cached mutual Maps', async () => {
        const worlds = [{ worldId: 'wrld_example', worldName: 'Example' }];
        const friends = [{ userId: 'usr_friend', displayName: 'Friend' }];
        webApi.getHotWorlds.mockResolvedValue(worlds);
        webApi.getHotWorldFriendDetail.mockResolvedValue(friends);
        webApi.getMutualGraphSnapshot.mockResolvedValue([{ friendId: 'usr_friend', mutualIds: ['usr_mutual'] }]);
        webApi.getMutualGraphMeta.mockResolvedValue([
            { friendId: 'usr_friend', lastFetchedAt: '2026-11-06T12:00:00Z', optedOut: false }
        ]);

        await expect(webDatabase.getHotWorlds(30, 30)).resolves.toEqual(worlds);
        await expect(webDatabase.getHotWorldFriendDetail('wrld_example', 30)).resolves.toEqual(friends);
        expect(webApi.getHotWorlds).toHaveBeenCalledWith(30, 30);
        expect(webApi.getHotWorldFriendDetail).toHaveBeenCalledWith('wrld_example', 30);

        const snapshot = await webDatabase.getMutualGraphSnapshot();
        const meta = await webDatabase.getMutualGraphMeta();
        expect(snapshot).toEqual(new Map([['usr_friend', ['usr_mutual']]]));
        expect(meta).toEqual(new Map([['usr_friend', { lastFetchedAt: '2026-11-06T12:00:00Z', optedOut: false }]]));
    });
});
