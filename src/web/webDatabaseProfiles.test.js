const mocks = vi.hoisted(() => ({ getFriends: vi.fn() }));

vi.mock('./api', () => ({
    webApi: { getFriends: (...args) => mocks.getFriends(...args) },
    itemsFrom: (payload) => (Array.isArray(payload) ? payload : payload?.items || [])
}));

describe('Web friend profile search data', () => {
    let webDatabase;

    beforeEach(async () => {
        vi.resetModules();
        mocks.getFriends.mockReset();
        ({ webDatabase } = await import('./webDatabase'));
    });

    it('retains bio and link search data across every friend page without another API request', async () => {
        const friends = Array.from({ length: 401 }, (_, index) => ({
            id: `usr_friend_${index}`,
            displayName: `Friend ${index}`,
            raw: {
                bio: `Searchable profile ${index}`,
                bioLinks: [`https://example.invalid/profile/${index}`]
            }
        }));
        mocks.getFriends.mockImplementation(async ({ offset, limit }) => ({
            items: friends.slice(offset, offset + limit),
            total: friends.length
        }));

        const hydratedFriends = await webDatabase.getWebFriends();
        const profiles = await webDatabase.getAllUserProfiles();

        expect(hydratedFriends).toHaveLength(401);
        expect(profiles).toEqual(
            friends.map((friend) => ({ userId: friend.id, bio: friend.raw.bio, bioLinks: friend.raw.bioLinks }))
        );
        expect(mocks.getFriends.mock.calls).toEqual([
            [{ offset: 0, limit: 200 }],
            [{ offset: 200, limit: 200 }],
            [{ offset: 400, limit: 200 }]
        ]);

        await expect(webDatabase.getAllUserProfiles()).resolves.toEqual(profiles);
        expect(mocks.getFriends).toHaveBeenCalledTimes(3);
    });

    it('returns only profile search fields from sanitized friend data', async () => {
        mocks.getFriends.mockResolvedValue({
            total: 2,
            items: [
                {
                    id: 'usr_friend',
                    displayName: 'Friend',
                    cookie: 'secret-top-level-cookie',
                    raw: {
                        bio: 'Useful public bio',
                        bioLinks: ['https://example.invalid/bio', { token: 'secret-nested-token' }, false],
                        password: 'secret-password',
                        friendKey: 'secret-friend-key',
                        accessToken: 'secret-access-token'
                    }
                },
                { id: 'usr_without_profile', raw: { bioLinks: null } }
            ]
        });

        const friends = await webDatabase.getWebFriends();
        const profiles = await webDatabase.getAllUserProfiles();

        expect(profiles).toEqual([
            { userId: 'usr_friend', bio: 'Useful public bio', bioLinks: ['https://example.invalid/bio'] },
            { userId: 'usr_without_profile', bio: '', bioLinks: [] }
        ]);
        expect(JSON.stringify(friends)).not.toContain('secret-');
        expect(JSON.stringify(profiles)).not.toContain('secret-');
        expect(mocks.getFriends).toHaveBeenCalledOnce();
    });

    it('replaces old profile data when the fixed friend projection refreshes', async () => {
        mocks.getFriends.mockResolvedValueOnce({
            total: 1,
            items: [{ id: 'usr_previous', raw: { bio: 'Old bio', bioLinks: ['https://example.invalid/old'] } }]
        });
        await webDatabase.getWebFriends();
        await expect(webDatabase.getAllUserProfiles()).resolves.toEqual([
            { userId: 'usr_previous', bio: 'Old bio', bioLinks: ['https://example.invalid/old'] }
        ]);

        mocks.getFriends.mockResolvedValueOnce({
            total: 1,
            items: [{ id: 'usr_current', raw: { bio: 'Current bio', bioLinks: ['https://example.invalid/current'] } }]
        });
        await webDatabase.getWebFriends();

        await expect(webDatabase.getAllUserProfiles()).resolves.toEqual([
            { userId: 'usr_current', bio: 'Current bio', bioLinks: ['https://example.invalid/current'] }
        ]);
        expect(mocks.getFriends).toHaveBeenCalledTimes(2);
    });

    it('does not fetch profiles before the friend projection has been loaded', async () => {
        await expect(webDatabase.getAllUserProfiles()).resolves.toEqual([]);
        expect(mocks.getFriends).not.toHaveBeenCalled();
    });
});
