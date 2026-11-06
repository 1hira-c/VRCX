import { describe, expect, it, vi } from 'vitest';

import { executeHistoryDatabaseRead } from '../serverHistoryReadControl';

function stores({ friends = new Map() } = {}) {
    return {
        friend: {
            friends,
            localFavoriteFriends: new Set(['usr_favorite'])
        }
    };
}

describe('serverHistoryReadControl', () => {
    it('invokes the original feed lookup with renderer-owned favorites', async () => {
        const database = {
            lookupFeedDatabase: vi.fn().mockResolvedValue([
                {
                    rowId: 1,
                    created_at: '2026-11-06T00:00:00Z',
                    type: 'Bio',
                    userId: 'usr_favorite',
                    displayName: 'Favorite',
                    bio: 'new bio',
                    previousBio: 'old bio',
                    bioLinks: ['https://example.invalid/new'],
                    previousBioLinks: ['https://example.invalid/old']
                }
            ])
        };

        const result = await executeHistoryDatabaseRead(stores(), database, {
            name: 'lookupFeedDatabase',
            arguments: { filters: ['Bio'], favoritesOnly: true, maxEntries: 500 }
        });

        expect(database.lookupFeedDatabase).toHaveBeenCalledWith(['Bio'], ['usr_favorite'], 500);
        expect(result[0]).toMatchObject({
            rowId: 1,
            type: 'Bio',
            bio: 'new bio',
            previousBio: 'old bio',
            bioLinks: ['https://example.invalid/new'],
            previousBioLinks: ['https://example.invalid/old']
        });
    });

    it('passes original feed search, date, and limit arguments without browser filtering', async () => {
        const database = { searchFeedDatabase: vi.fn().mockResolvedValue([]) };
        await executeHistoryDatabaseRead(stores(), database, {
            name: 'searchFeedDatabase',
            arguments: {
                search: 'public',
                filters: ['Avatar'],
                favoritesOnly: false,
                maxEntries: 50_000,
                dateFrom: '2026-10-01T00:00:00Z',
                dateTo: '2026-11-01T00:00:00Z'
            }
        });
        expect(database.searchFeedDatabase).toHaveBeenCalledWith(
            'public',
            ['Avatar'],
            [],
            50_000,
            '2026-10-01T00:00:00Z',
            '2026-11-01T00:00:00Z'
        );
    });

    it('serializes the original previous-display-name Map for profile stats', async () => {
        const database = {
            getUserStats: vi.fn().mockResolvedValue({
                timeSpent: 120_000,
                lastSeen: '2026-11-06T00:00:00Z',
                joinCount: 3,
                userId: 'usr_example',
                previousDisplayNames: new Map([['Old Name', '2026-10-01T00:00:00Z']])
            })
        };
        const result = await executeHistoryDatabaseRead(stores(), database, {
            name: 'getUserStats',
            arguments: { userId: 'usr_example', displayName: 'New Name', inCurrentWorld: false }
        });
        expect(database.getUserStats).toHaveBeenCalledWith({ id: 'usr_example', displayName: 'New Name' }, false);
        expect(result.previousDisplayNames).toEqual([{ displayName: 'Old Name', updatedAt: '2026-10-01T00:00:00Z' }]);
    });

    it('uses the original current-friend table for stale imported bulk stats', async () => {
        const database = {
            getFriendLogCurrent: vi.fn().mockResolvedValue([
                { userId: 'usr_imported', displayName: 'Imported Friend' },
                { userId: "usr_invalid'", displayName: 'Rejected ID' }
            ]),
            getAllUserStats: vi.fn().mockResolvedValue([
                {
                    lastSeen: '2026-11-01T00:00:00Z',
                    userId: 'usr_imported',
                    timeSpent: 60_000,
                    joinCount: 2,
                    displayName: 'Imported Friend'
                }
            ])
        };
        const result = await executeHistoryDatabaseRead(stores(), database, {
            name: 'getAllUserStats',
            arguments: {}
        });
        expect(database.getAllUserStats).toHaveBeenCalledWith(['usr_imported'], ['Imported Friend', 'Rejected ID']);
        expect(result).toHaveLength(1);
        expect(result[0]).toMatchObject({ userId: 'usr_imported', joinCount: 2 });
    });
});
