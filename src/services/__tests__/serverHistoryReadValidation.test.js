import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const {
    normalizeHistoryDatabaseReadResult,
    validateHistoryDatabaseReadArguments
} = require('../../../src-electron/serverHistoryReadValidation.js');

function feedRow(overrides = {}) {
    return {
        rowId: 7,
        created_at: '2026-11-06T12:00:00Z',
        userId: 'usr_example',
        displayName: 'Example User',
        type: 'Avatar',
        location: null,
        worldName: null,
        previousLocation: null,
        time: null,
        groupName: null,
        status: null,
        statusDescription: null,
        previousStatus: null,
        previousStatusDescription: null,
        bio: null,
        previousBio: null,
        bioLinks: ['https://example.invalid/current'],
        previousBioLinks: ['https://example.invalid/previous'],
        ownerId: 'usr_example',
        avatarName: 'Example Avatar',
        currentAvatarImageUrl: 'https://example.invalid/avatar.png',
        currentAvatarThumbnailImageUrl: 'https://example.invalid/avatar-thumb.png',
        previousCurrentAvatarImageUrl: 'https://example.invalid/old-avatar.png',
        previousCurrentAvatarThumbnailImageUrl: 'https://example.invalid/old-avatar-thumb.png',
        ...overrides
    };
}

describe('server history database-read validation', () => {
    it('accepts only the fixed feed lookup/search arguments used by original VRCX', () => {
        expect(
            validateHistoryDatabaseReadArguments('searchFeedDatabase', {
                search: '',
                filters: ['GPS', 'Avatar'],
                favoritesOnly: true,
                maxEntries: 50_000,
                dateFrom: '2026-11-01T00:00:00Z',
                dateTo: '2026-11-06T00:00:00Z'
            })
        ).toEqual({
            search: '',
            filters: ['GPS', 'Avatar'],
            favoritesOnly: true,
            maxEntries: 50_000,
            dateFrom: '2026-11-01T00:00:00Z',
            dateTo: '2026-11-06T00:00:00Z'
        });

        expect(
            validateHistoryDatabaseReadArguments('searchFeedDatabase', {
                search: '',
                filters: [],
                favoritesOnly: false,
                maxEntries: 500,
                dateFrom: '2026-11-07T00:00:00Z',
                dateTo: '2026-11-06T00:00:00Z'
            })
        ).toBeNull();
        expect(
            validateHistoryDatabaseReadArguments('lookupFeedDatabase', {
                filters: [],
                favoritesOnly: false,
                maxEntries: 500,
                sql: 'SELECT * FROM cookies'
            })
        ).toBeNull();
        expect(validateHistoryDatabaseReadArguments('executeSql', {})).toBeNull();
    });

    it('preserves the original feed fields while rejecting unknown response data', () => {
        const args = { filters: ['Avatar'], favoritesOnly: false, maxEntries: 1 };
        expect(normalizeHistoryDatabaseReadResult('lookupFeedDatabase', [feedRow()], args)).toEqual([feedRow()]);
        expect(() =>
            normalizeHistoryDatabaseReadResult(
                'lookupFeedDatabase',
                [feedRow({ cookie: 'must-not-cross-boundary' })],
                args
            )
        ).toThrow('Invalid renderer history database-read response');
        expect(() =>
            normalizeHistoryDatabaseReadResult('lookupFeedDatabase', [feedRow(), feedRow({ rowId: 8 })], args)
        ).toThrow('Invalid renderer history database-read response');
    });

    it('binds memo and per-user history responses to the requested entity', () => {
        expect(
            normalizeHistoryDatabaseReadResult(
                'getUserMemo',
                { userId: 'usr_example', editedAt: '2026-11-06T00:00:00Z', memo: 'memo' },
                { userId: 'usr_example' }
            )
        ).toMatchObject({ userId: 'usr_example', memo: 'memo' });
        expect(() =>
            normalizeHistoryDatabaseReadResult(
                'getUserMemo',
                { userId: 'usr_other', editedAt: null, memo: 'wrong user' },
                { userId: 'usr_example' }
            )
        ).toThrow('Invalid renderer history database-read response');
        expect(() =>
            normalizeHistoryDatabaseReadResult(
                'getFriendLogHistoryForUserId',
                [
                    {
                        rowId: 1,
                        created_at: '2026-11-06T00:00:00Z',
                        type: 'Friend',
                        userId: 'usr_other',
                        displayName: 'Other',
                        previousDisplayName: null,
                        trustLevel: null,
                        previousTrustLevel: null,
                        friendNumber: 1
                    }
                ],
                { userId: 'usr_example', types: [] }
            )
        ).toThrow('Invalid renderer history database-read response');
    });
});
