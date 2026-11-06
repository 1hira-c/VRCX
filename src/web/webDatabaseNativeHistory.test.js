import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    getFeed: vi.fn(),
    getGameLog: vi.fn(),
    getNativeFeed: vi.fn(),
    getNativeGameLog: vi.fn(),
    getNativeUserStats: vi.fn()
}));

vi.mock('./api', () => ({
    webApi: {
        getFeed: (...args) => mocks.getFeed(...args),
        getGameLog: (...args) => mocks.getGameLog(...args),
        getNativeFeed: (...args) => mocks.getNativeFeed(...args),
        getNativeGameLog: (...args) => mocks.getNativeGameLog(...args),
        getNativeUserStats: (...args) => mocks.getNativeUserStats(...args)
    }
}));

import { webDatabase } from './webDatabase';

describe('webDatabase native history adapter', () => {
    beforeEach(() => {
        for (const mock of Object.values(mocks)) mock.mockReset();
    });

    it('uses the fixed native feed lookup and preserves server ordering without projection pagination', async () => {
        const rows = [
            { rowId: 9, created_at: '2026-11-06T00:00:00Z', type: 'Avatar' },
            { rowId: 4, created_at: '2026-11-05T00:00:00Z', type: 'GPS' }
        ];
        mocks.getNativeFeed.mockResolvedValue(rows);

        await expect(webDatabase.lookupFeedDatabase(['Avatar'], ['usr_favorite'], 250)).resolves.toBe(rows);
        expect(mocks.getNativeFeed).toHaveBeenCalledWith({
            filters: ['Avatar'],
            favoritesOnly: true,
            maxEntries: 250
        });
        expect(mocks.getFeed).not.toHaveBeenCalled();
    });

    it('passes feed date search and game-log search to their original server-side methods', async () => {
        mocks.getNativeFeed.mockResolvedValue([]);
        mocks.getNativeGameLog.mockResolvedValue([]);

        await webDatabase.searchFeedDatabase(
            'public',
            ['Avatar'],
            [],
            50_000,
            '2026-11-01T00:00:00Z',
            '2026-11-06T00:00:00Z'
        );
        expect(mocks.getNativeFeed).toHaveBeenCalledWith({
            search: 'public',
            filters: ['Avatar'],
            favoritesOnly: false,
            maxEntries: 50_000,
            dateFrom: '2026-11-01T00:00:00Z',
            dateTo: '2026-11-06T00:00:00Z'
        });

        await webDatabase.searchGameLogDatabase('Example World', ['Location'], ['usr_favorite'], 50_000);
        expect(mocks.getNativeGameLog).toHaveBeenCalledWith({
            search: 'Example World',
            filters: ['Location'],
            favoritesOnly: true,
            maxEntries: 50_000
        });
        expect(mocks.getGameLog).not.toHaveBeenCalled();
    });

    it('reconstructs the original previous-display-name Map for the profile UI', async () => {
        mocks.getNativeUserStats.mockResolvedValue({
            timeSpent: 90_000,
            lastSeen: '2026-11-06T00:00:00Z',
            joinCount: 4,
            userId: 'usr_example',
            previousDisplayNames: [{ displayName: 'Old Name', updatedAt: '2026-10-01T00:00:00Z' }]
        });

        const result = await webDatabase.getUserStats({ id: 'usr_example', displayName: 'Current Name' }, true);
        expect(mocks.getNativeUserStats).toHaveBeenCalledWith('usr_example', 'Current Name', true);
        expect(result.previousDisplayNames).toEqual(new Map([['Old Name', '2026-10-01T00:00:00Z']]));
    });
});
