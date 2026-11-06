import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    watchState: { isLoggedIn: true },
    getPublicProfile: vi.fn(),
    getRepresentedGroup: vi.fn(),
    getGroups: vi.fn(),
    getWorlds: vi.fn(),
    getFavoriteGroups: vi.fn(),
    getFavoriteWorlds: vi.fn(),
    getAvatars: vi.fn(),
    getLicensedAvatars: vi.fn(),
    lookupAvatars: vi.fn(),
    getGroupPosts: vi.fn(),
    getGroupMember: vi.fn(),
    getGroupMembers: vi.fn(),
    getGroupMembersSearch: vi.fn(),
    getGroupGallery: vi.fn(),
    getAvatarGallery: vi.fn(),
    getInstance: vi.fn()
}));

vi.mock('../../api', () => ({
    userRequest: { getPublicProfile: (...args) => mocks.getPublicProfile(...args) },
    worldRequest: { getWorlds: (...args) => mocks.getWorlds(...args) },
    favoriteRequest: {
        getFavoriteGroups: (...args) => mocks.getFavoriteGroups(...args),
        getFavoriteWorlds: (...args) => mocks.getFavoriteWorlds(...args)
    },
    avatarRequest: {
        getAvatars: (...args) => mocks.getAvatars(...args),
        getLicensedAvatars: (...args) => mocks.getLicensedAvatars(...args),
        getAvatarGallery: (...args) => mocks.getAvatarGallery(...args)
    },
    groupRequest: {
        getRepresentedGroup: (...args) => mocks.getRepresentedGroup(...args),
        getGroups: (...args) => mocks.getGroups(...args),
        getGroupPosts: (...args) => mocks.getGroupPosts(...args),
        getGroupMember: (...args) => mocks.getGroupMember(...args),
        getGroupMembers: (...args) => mocks.getGroupMembers(...args),
        getGroupMembersSearch: (...args) => mocks.getGroupMembersSearch(...args),
        getGroupGallery: (...args) => mocks.getGroupGallery(...args)
    },
    instanceRequest: { getInstance: (...args) => mocks.getInstance(...args) }
}));

vi.mock('../../coordinators/avatarCoordinator', () => ({
    lookupAvatars: (...args) => mocks.lookupAvatars(...args)
}));

vi.mock('../watchState', () => ({ watchState: mocks.watchState }));

import { entityResource, entityResourceSelectors } from '../serverEntityResourceControl';

const stores = { user: { currentUser: { id: 'usr_current' } } };

describe('serverEntityResourceControl', () => {
    beforeEach(() => {
        mocks.watchState.isLoggedIn = true;
        for (const mock of Object.values(mocks)) {
            if (typeof mock?.mockReset === 'function') mock.mockReset();
        }
    });

    it('retains meaningful public-profile theme and badge context but drops unknown secrets', () => {
        const selected = entityResourceSelectors.publicProfile({
            id: 'usr_target',
            displayName: 'Target',
            bio: 'About Target',
            bioLinks: ['https://example.com'],
            themeButtonColor: '112233',
            themeIconColor: '445566',
            themeSubtextColor: '778899',
            badges: [{ badgeId: 'bdg_1', badgeName: 'Contributor', badgeImageUrl: 'https://img' }],
            password: 'drop-password',
            accessToken: 'drop-token',
            internalProfileValue: 'drop-unknown'
        });

        expect(selected).toMatchObject({
            id: 'usr_target',
            displayName: 'Target',
            bio: 'About Target',
            bioLinks: ['https://example.com'],
            themeButtonColor: '112233',
            badges: [{ badgeId: 'bdg_1', badgeName: 'Contributor' }]
        });
        expect(JSON.stringify(selected)).not.toContain('drop-');
    });

    it('uses original group APIs and preserves who, roles, images and visibility', async () => {
        mocks.getGroupPosts.mockResolvedValue({
            json: {
                total: 1,
                posts: [
                    {
                        id: 'not_1',
                        groupId: 'grp_example',
                        authorId: 'usr_author',
                        editorId: 'usr_editor',
                        title: 'Announcement',
                        text: 'Meet here',
                        imageId: 'file_1',
                        imageUrl: 'https://img',
                        visibility: 'group',
                        roleIds: ['grol_members'],
                        createdAt: '2026-11-01T00:00:00Z',
                        cookie: 'drop-cookie'
                    }
                ]
            }
        });

        const response = await entityResource(stores, {
            name: 'groupPosts',
            arguments: { groupId: 'grp_example', n: 100, offset: 0 }
        });

        expect(mocks.getGroupPosts).toHaveBeenCalledWith({ groupId: 'grp_example', n: 100, offset: 0 });
        expect(response).toMatchObject({
            ok: true,
            result: {
                total: 1,
                posts: [
                    {
                        groupId: 'grp_example',
                        authorId: 'usr_author',
                        editorId: 'usr_editor',
                        imageUrl: 'https://img',
                        visibility: 'group',
                        roleIds: ['grol_members']
                    }
                ]
            }
        });
        expect(JSON.stringify(response)).not.toContain('drop-cookie');
    });

    it('keeps member identity and role context while selecting the nested user', () => {
        const selected = entityResourceSelectors.groupMember({
            id: 'gmem_1',
            groupId: 'grp_example',
            userId: 'usr_member',
            joinedAt: '2026-10-01T00:00:00Z',
            roleIds: ['grol_reader'],
            managerNotes: 'Trusted host',
            user: {
                id: 'usr_member',
                displayName: 'Member',
                iconUrl: 'https://icon',
                profileEffect: 'effect_1',
                credential: 'drop-credential'
            },
            sessionToken: 'drop-token'
        });

        expect(selected).toMatchObject({
            id: 'gmem_1',
            groupId: 'grp_example',
            userId: 'usr_member',
            roleIds: ['grol_reader'],
            managerNotes: 'Trusted host',
            user: { id: 'usr_member', displayName: 'Member', iconUrl: 'https://icon' }
        });
        expect(JSON.stringify(selected)).not.toContain('drop-');
    });

    it('keeps only gallery download data needed by the original carousel', () => {
        const selected = entityResourceSelectors.galleryFile({
            id: 'file_gallery',
            name: 'Gallery image',
            ownerId: 'usr_owner',
            tags: ['avatargallery'],
            versions: [
                {
                    version: 1,
                    status: 'complete',
                    file: {
                        url: 'https://cdn.example/image.png',
                        fileName: 'image.png',
                        sizeInBytes: 1234,
                        status: 'complete',
                        uploadId: 'drop-upload-id',
                        md5: 'drop-md5'
                    },
                    signature: { url: 'drop-signature-url' }
                }
            ]
        });

        expect(selected.versions[0]).toEqual({
            version: 1,
            status: 'complete',
            file: {
                url: 'https://cdn.example/image.png',
                fileName: 'image.png',
                sizeInBytes: 1234,
                status: 'complete'
            }
        });
        expect(JSON.stringify(selected)).not.toContain('drop-');
    });

    it('allows public and group-public instances but rejects private locations and nonce context', () => {
        expect(
            entityResourceSelectors.publicWorldInstance({
                id: '12345',
                instanceId: '12345',
                worldId: 'wrld_example',
                location: 'wrld_example:12345~region(jp)',
                type: 'public',
                n_users: 12,
                nonce: 'drop-nonce'
            })
        ).toMatchObject({ worldId: 'wrld_example', type: 'public', n_users: 12 });

        expect(
            entityResourceSelectors.publicWorldInstance({
                location: 'wrld_example:12345~group(grp_example)~groupAccessType(public)',
                type: 'group',
                groupAccessType: 'public'
            })
        ).toMatchObject({ type: 'group', groupAccessType: 'public' });

        expect(
            entityResourceSelectors.publicWorldInstance({
                location: 'wrld_example:12345~private(usr_owner)~nonce(secret)',
                type: 'private'
            })
        ).toBeNull();
    });

    it('never calls upstream resources without the original authenticated worker session', async () => {
        mocks.watchState.isLoggedIn = false;
        const response = await entityResource(stores, {
            name: 'userGroups',
            arguments: { userId: 'usr_target' }
        });

        expect(response).toEqual({ ok: false, result: null, errorCode: 'requires_authentication' });
        expect(mocks.getGroups).not.toHaveBeenCalled();
    });
});
