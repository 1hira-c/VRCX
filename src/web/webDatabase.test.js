import { describe, expect, it } from 'vitest';

import {
    toFeedRow,
    toGameLogRow,
    toNotificationRow,
    toWebFavorite,
    toWebFavoriteGroup,
    toWebUser
} from './webDatabase';

describe('web read model projections', () => {
    it('keeps the useful invite context while discarding unapproved nested data', () => {
        const row = toNotificationRow({
            id: 'noty_example',
            type: 'invite',
            createdAt: '2026-11-04T00:00:00Z',
            payload: {
                senderUserId: 'usr_sender',
                senderUsername: 'Sender',
                targetUserId: 'usr_target',
                targetUsername: 'Target',
                details: {
                    worldId: 'wrld_example',
                    worldName: 'Useful World',
                    location: 'wrld_example:12345~friends',
                    inviteMessage: 'Come join us',
                    cookie: 'must-not-reach-the-view',
                    signedUrl: 'https://files.vrchat.cloud/secret'
                }
            }
        });

        expect(row).toMatchObject({
            senderUserId: 'usr_sender',
            senderUsername: 'Sender',
            targetUserId: 'usr_target',
            targetUsername: 'Target',
            worldId: 'wrld_example',
            worldName: 'Useful World',
            location: 'wrld_example:12345~friends'
        });
        expect(row.details.inviteMessage).toBe('Come join us');
        expect(JSON.stringify(row)).not.toContain('must-not-reach-the-view');
        expect(JSON.stringify(row)).not.toContain('files.vrchat.cloud/secret');
    });

    it('keeps meaningful v2 participants and destinations without exposing actions or unknown fields', () => {
        const row = toNotificationRow({
            id: 'noty_v2',
            type: 'group.announcement',
            createdAt: '2026-11-04T00:00:00Z',
            title: 'Announcement',
            message: 'Meet here',
            seen: false,
            payload: {
                version: 2,
                updatedAt: '2026-11-04T00:01:00Z',
                expiresAt: '2026-11-05T00:00:00Z',
                link: 'group:grp_example',
                linkText: 'Open group',
                imageUrl: 'https://example.invalid/group.png',
                senderUserId: 'usr_sender',
                senderUsername: 'Sender',
                receiverUserId: 'usr_receiver',
                worldId: 'wrld_example',
                worldName: 'Example World',
                location: 'wrld_example:12345~group(grp_example)',
                inviteMessage: 'Join us',
                requestMessage: 'Can I join?',
                responseMessage: 'See you there',
                data: {
                    announcementTitle: 'Meetup',
                    groupId: 'grp_example',
                    groupName: 'Example Group',
                    ownerName: 'Owner',
                    password: 'drop-password',
                    accessToken: 'drop-token',
                    cookie: 'drop-cookie',
                    nested: { value: 'drop-nested' }
                },
                details: {
                    emojiId: 'emoji_wave',
                    emojiVersion: 2,
                    inventoryItemId: 'inv_example',
                    targetUserId: 'usr_target',
                    targetDisplayName: 'Target',
                    password: 'drop-password',
                    token: 'drop-token',
                    cookie: 'drop-cookie',
                    unknown: 'drop-unknown'
                },
                responses: [
                    {
                        type: 'link',
                        text: 'Unsafe action',
                        data: 'https://example.invalid/action',
                        icon: 'reply'
                    }
                ]
            }
        });

        expect(row).toMatchObject({
            version: 2,
            senderUserId: 'usr_sender',
            senderUsername: 'Sender',
            recipientUserId: 'usr_receiver',
            receiverUserId: 'usr_receiver',
            targetUserId: 'usr_target',
            targetUsername: 'Target',
            worldId: 'wrld_example',
            worldName: 'Example World',
            location: 'wrld_example:12345~group(grp_example)',
            groupId: 'grp_example',
            groupName: 'Example Group',
            inviteMessage: 'Join us',
            requestMessage: 'Can I join?',
            responseMessage: 'See you there',
            link: 'group:grp_example',
            linkText: 'Open group',
            imageUrl: 'https://example.invalid/group.png',
            responses: []
        });
        expect(row.data).toEqual({
            ownerName: 'Owner',
            announcementTitle: 'Meetup',
            groupId: 'grp_example',
            groupName: 'Example Group'
        });
        expect(row.details).toMatchObject({
            targetUserId: 'usr_target',
            targetDisplayName: 'Target',
            imageUrl: 'https://example.invalid/group.png',
            emojiId: 'emoji_wave',
            emojiVersion: 2,
            inventoryItemId: 'inv_example'
        });
        expect(JSON.stringify(row)).not.toContain('drop-');
        expect(JSON.stringify(row)).not.toContain('Unsafe action');
        expect(JSON.stringify(row)).not.toContain('example.invalid/action');
    });

    it('restores exact remote favorites and groups into the existing VRCX model', () => {
        const favorite = toWebFavorite({
            id: 'fvrt_example',
            type: 'vrcPlusWorld',
            favoriteId: 'wrld_example',
            tags: ['vrcPlusWorlds1'],
            groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
            sortOrder: 7,
            targetName: 'Example World',
            targetPayload: {
                id: 'wrld_example',
                name: 'Example World',
                authorName: 'Creator',
                cookie: 'must-not-reach-the-view'
            }
        });

        expect(favorite).toMatchObject({
            id: 'fvrt_example',
            type: 'vrcPlusWorld',
            favoriteId: 'wrld_example',
            tags: ['vrcPlusWorlds1'],
            $groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
            sortOrder: 7,
            targetName: 'Example World'
        });
        expect(favorite.targetPayload).toMatchObject({ name: 'Example World', authorName: 'Creator' });
        expect(JSON.stringify(favorite)).not.toContain('must-not-reach-the-view');

        const group = toWebFavoriteGroup({
            key: 'vrcPlusWorld:vrcPlusWorlds1',
            type: 'vrcPlusWorld',
            name: 'vrcPlusWorlds1',
            displayName: 'VRC+ Group 1',
            capacity: 100,
            count: 1,
            visibility: 'private',
            sortOrder: 3,
            token: 'must-not-reach-the-view'
        });
        expect(group).toEqual({
            key: 'vrcPlusWorld:vrcPlusWorlds1',
            type: 'vrcPlusWorld',
            name: 'vrcPlusWorlds1',
            displayName: 'VRC+ Group 1',
            capacity: 100,
            count: 1,
            visibility: 'private',
            sortOrder: 3
        });
        expect(JSON.stringify(group)).not.toContain('must-not-reach-the-view');
    });

    it('does not spread arbitrary raw friend fields into the existing user model', () => {
        const user = toWebUser({
            id: 'usr_example',
            displayName: 'Example User',
            location: 'offline',
            raw: {
                bio: 'Visible bio',
                bioLinks: ['https://example.com'],
                cookie: 'secret-cookie',
                credentials: { password: 'drop-me' }
            }
        });

        expect(user).toMatchObject({
            id: 'usr_example',
            displayName: 'Example User',
            bio: 'Visible bio',
            bioLinks: ['https://example.com']
        });
        expect(JSON.stringify(user)).not.toContain('secret-cookie');
        expect(JSON.stringify(user)).not.toContain('drop-me');
    });

    it('preserves the original friend order without inventing one', () => {
        expect(
            toWebUser({
                id: 'usr_friend',
                displayName: 'Friend',
                raw: { friendNumber: 37 }
            }).friendNumber
        ).toBe(37);
        expect(toWebUser({ id: 'usr_unknown', displayName: 'Unknown', raw: {} })).not.toHaveProperty('friendNumber');
    });

    it('keeps feed details which the original VRCX columns render', () => {
        const row = toFeedRow({
            id: 10,
            kind: 'avatar',
            payload: {
                currentAvatarImageUrl: 'https://example.invalid/current.png',
                currentAvatarThumbnailImageUrl: 'https://example.invalid/current-thumb.png',
                previousCurrentAvatarImageUrl: 'https://example.invalid/previous.png',
                previousCurrentAvatarThumbnailImageUrl: 'https://example.invalid/previous-thumb.png',
                bioLinks: ['https://example.invalid/current'],
                previousBioLinks: ['https://example.invalid/previous']
            }
        });

        expect(row).toMatchObject({
            currentAvatarImageUrl: 'https://example.invalid/current.png',
            currentAvatarThumbnailImageUrl: 'https://example.invalid/current-thumb.png',
            previousCurrentAvatarImageUrl: 'https://example.invalid/previous.png',
            previousCurrentAvatarThumbnailImageUrl: 'https://example.invalid/previous-thumb.png',
            bioLinks: ['https://example.invalid/current'],
            previousBioLinks: ['https://example.invalid/previous']
        });
    });

    it('keeps game log duration and resource targets used by the original views', () => {
        expect(
            toGameLogRow({
                id: 11,
                kind: 'video_play',
                payload: { time: 120, videoUrl: 'https://example.invalid/video' }
            })
        ).toMatchObject({ time: 120, videoUrl: 'https://example.invalid/video' });
        expect(
            toGameLogRow({
                id: 12,
                kind: 'resource_load',
                payload: { resourceUrl: 'https://example.invalid/resource', resourceType: 'ImageLoad' }
            })
        ).toMatchObject({ resourceUrl: 'https://example.invalid/resource', resourceType: 'ImageLoad' });
    });
});
