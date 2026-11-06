import { buildServerProjectionSnapshot, sanitizeProjectionValue } from '../serverProjection';

describe('serverProjection', () => {
    it('removes credentials recursively while retaining rich profile and location data', () => {
        const value = sanitizeProjectionValue({
            id: 'usr_friend',
            displayName: 'Friend',
            friendKey: 'secret-friend-key',
            authorId: 'usr_author',
            location: 'wrld_test:123~region(jp)',
            profile: {
                bio: 'hello',
                password: 'secret-password',
                nested: { cookie: 'auth-cookie', accessToken: 'bearer-token' }
            }
        });

        expect(value).toMatchObject({
            id: 'usr_friend',
            displayName: 'Friend',
            authorId: 'usr_author',
            location: 'wrld_test:123~region(jp)',
            profile: { bio: 'hello', nested: {} }
        });
        expect(JSON.stringify(value)).not.toContain('secret-friend-key');
        expect(JSON.stringify(value)).not.toContain('secret-password');
        expect(JSON.stringify(value)).not.toContain('auth-cookie');
        expect(JSON.stringify(value)).not.toContain('bearer-token');
    });

    it('always projects the current user and friends with deterministic payloads', () => {
        const stores = {
            user: {
                currentUser: {
                    id: 'usr_me',
                    displayName: 'Me',
                    state: 'online',
                    isFriend: true,
                    location: 'wrld_home:1'
                },
                cachedUsers: new Map([
                    [
                        'usr_friend',
                        {
                            id: 'usr_friend',
                            displayName: 'Friend',
                            state: 'online',
                            location: 'wrld_party:2',
                            worldName: 'Party',
                            bioLinks: ['https://example.invalid/profile'],
                            badges: [
                                {
                                    badgeId: 'bdg_example',
                                    badgeName: 'Example',
                                    badgeImageUrl: 'https://api.vrchat.cloud/api/1/file/example'
                                }
                            ],
                            unknownFuturePrivateField: 'must-not-pass-the-allowlist',
                            friendKey: 'must-not-leak'
                        }
                    ]
                ]),
                cachedProfiles: new Map([['usr_friend', { id: 'usr_friend', bio: 'Rich profile' }]])
            },
            friend: {
                friends: new Map([
                    [
                        'usr_friend',
                        {
                            state: 'online',
                            name: 'Friend',
                            ref: {
                                id: 'usr_friend',
                                displayName: 'Friend',
                                location: 'wrld_party:2'
                            }
                        }
                    ]
                ])
            },
            world: {
                cachedWorlds: new Map([
                    ['wrld_party', { id: 'wrld_party', name: 'Party', authorId: 'usr_author', description: 'World' }]
                ])
            },
            avatar: { cachedAvatars: new Map() },
            group: { cachedGroups: new Map(), currentUserGroups: new Map() },
            favorite: {
                cachedFavorites: new Map([
                    [
                        'fvrt_world',
                        {
                            id: 'fvrt_world',
                            type: 'vrcPlusWorld',
                            favoriteId: 'wrld_party',
                            tags: ['vrcPlusWorlds1'],
                            $groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
                            password: 'must-not-pass-the-allowlist'
                        }
                    ],
                    [
                        'fvrt_friend',
                        {
                            id: 'fvrt_friend',
                            type: 'friend',
                            favoriteId: 'usr_friend',
                            tags: ['group_0'],
                            $groupKey: 'friend:group_0',
                            unknownFutureField: 'must-not-pass-the-allowlist'
                        }
                    ]
                ]),
                favoritesSortOrder: ['usr_friend', 'wrld_party'],
                favoriteFriendGroups: [
                    {
                        key: 'friend:group_0',
                        type: 'friend',
                        name: 'group_0',
                        displayName: 'Best friends',
                        capacity: 150,
                        count: 1,
                        visibility: 'private',
                        token: 'must-not-pass-the-allowlist'
                    }
                ],
                favoriteWorldGroups: [
                    {
                        key: 'vrcPlusWorld:vrcPlusWorlds1',
                        type: 'vrcPlusWorld',
                        name: 'vrcPlusWorlds1',
                        displayName: 'VRC+ Group 1',
                        capacity: 100,
                        count: 1,
                        visibility: 'private'
                    }
                ],
                favoriteAvatarGroups: []
            }
        };

        const first = buildServerProjectionSnapshot(stores, '2026-01-01T00:00:00.000Z');
        const second = buildServerProjectionSnapshot(stores, '2026-01-01T00:01:00.000Z');
        const me = first.users.find((user) => user.userId === 'usr_me');
        const friend = first.users.find((user) => user.userId === 'usr_friend');

        expect(first.currentUserId).toBe('usr_me');
        expect(me.isFriend).toBe(false);
        expect(friend).toMatchObject({
            displayName: 'Friend',
            state: 'online',
            isFriend: true,
            payload: {
                bio: 'Rich profile',
                location: 'wrld_party:2',
                worldName: 'Party',
                bioLinks: ['https://example.invalid/profile'],
                badges: [{ badgeId: 'bdg_example', badgeName: 'Example' }]
            }
        });
        expect(JSON.stringify(friend.payload)).not.toContain('must-not-leak');
        expect(JSON.stringify(friend.payload)).not.toContain('must-not-pass-the-allowlist');
        expect(first.entities[0]).toMatchObject({
            entityType: 'world',
            entityId: 'wrld_party',
            ownerId: 'usr_author',
            description: 'World'
        });
        expect(first.favorites).toEqual([
            expect.objectContaining({
                id: 'fvrt_friend',
                type: 'friend',
                favoriteId: 'usr_friend',
                tags: ['group_0'],
                groupKey: 'friend:group_0',
                sortOrder: 0,
                targetName: 'Friend'
            }),
            expect.objectContaining({
                id: 'fvrt_world',
                type: 'vrcPlusWorld',
                favoriteId: 'wrld_party',
                tags: ['vrcPlusWorlds1'],
                groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
                sortOrder: 1,
                targetName: 'Party'
            })
        ]);
        expect(first.favoriteGroups).toEqual([
            {
                key: 'friend:group_0',
                type: 'friend',
                name: 'group_0',
                displayName: 'Best friends',
                capacity: 150,
                count: 1,
                visibility: 'private',
                sortOrder: 0
            },
            {
                key: 'vrcPlusWorld:vrcPlusWorlds1',
                type: 'vrcPlusWorld',
                name: 'vrcPlusWorlds1',
                displayName: 'VRC+ Group 1',
                capacity: 100,
                count: 1,
                visibility: 'private',
                sortOrder: 1
            }
        ]);
        expect(JSON.stringify(first.favorites)).not.toContain('must-not-pass-the-allowlist');
        expect(JSON.stringify(first.favoriteGroups)).not.toContain('must-not-pass-the-allowlist');
        expect(first.favoriteFingerprint).toBe(second.favoriteFingerprint);
        expect(first.fingerprint).toBe(second.fingerprint);
    });
});
