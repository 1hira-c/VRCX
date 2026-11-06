import { describe, expect, it } from 'vitest';

import { mapWebEntityDocument } from './entities';

describe('fixed web entity projection', () => {
    it('keeps meaningful user profile fields without copying arbitrary payload data', () => {
        const user = mapWebEntityDocument(
            {
                type: 'user',
                id: 'usr_example',
                name: 'Example User',
                payload: {
                    username: 'example',
                    bio: 'Visible bio',
                    status: 'active',
                    location: 'wrld_example:12345~friends',
                    tags: ['system_trust_known'],
                    bioLinks: ['https://example.com'],
                    badges: [{ badgeId: 'bdg_example', badgeName: 'Visible badge', privateToken: 'drop-me' }],
                    cookie: 'secret-cookie',
                    arbitrary: { nested: 'drop-me' }
                }
            },
            'user'
        );

        expect(user).toMatchObject({
            id: 'usr_example',
            displayName: 'Example User',
            username: 'example',
            bio: 'Visible bio',
            location: 'wrld_example:12345~friends',
            bioLinks: ['https://example.com']
        });
        expect(user.badges[0]).toEqual({ badgeId: 'bdg_example', badgeName: 'Visible badge' });
        expect(JSON.stringify(user)).not.toContain('secret-cookie');
        expect(JSON.stringify(user)).not.toContain('drop-me');
    });

    it.each([
        [
            'world',
            'wrld_example',
            {
                authorId: 'usr_author',
                authorName: 'World Author',
                capacity: 40,
                tags: ['system_approved'],
                slimInstances: [
                    {
                        id: '12345',
                        instanceId: '12345',
                        n_users: 12,
                        type: 'public',
                        region: 'jp',
                        secret: 'drop'
                    }
                ],
                unityPackages: [{ platform: 'standalonewindows', unityVersion: '2022.3.22f1', secret: 'drop' }]
            },
            {
                authorName: 'World Author',
                capacity: 40,
                slimInstances: [{ id: '12345', instanceId: '12345', n_users: 12, type: 'public', region: 'jp' }]
            }
        ],
        [
            'avatar',
            'avtr_example',
            {
                authorId: 'usr_author',
                authorName: 'Avatar Author',
                releaseStatus: 'public',
                styles: { primary: 'anime', secondary: 'cute', supplementary: ['colorful'], secret: 'drop' },
                publishedListings: [{ displayName: 'Avatar Listing', priceTokens: 1200, secret: 'drop' }]
            },
            {
                authorName: 'Avatar Author',
                releaseStatus: 'public',
                styles: { primary: 'anime', secondary: 'cute', supplementary: ['colorful'] }
            }
        ],
        [
            'group',
            'grp_example',
            {
                ownerId: 'usr_owner',
                shortCode: 'VRCX',
                memberCount: 42,
                rules: 'Be kind',
                roles: [{ id: 'grol_example', name: 'Member', permissions: ['group-instance-join'], secret: 'drop' }],
                galleries: [
                    {
                        id: 'ggal_example',
                        name: 'Community Photos',
                        membersOnly: true,
                        roleIdsToView: ['grol_example'],
                        secret: 'drop'
                    }
                ]
            },
            {
                ownerId: 'usr_owner',
                memberCount: 42,
                rules: 'Be kind',
                galleries: [
                    {
                        id: 'ggal_example',
                        name: 'Community Photos',
                        membersOnly: true,
                        roleIdsToView: ['grol_example']
                    }
                ]
            }
        ]
    ])('maps useful %s detail fields through a bounded projection', (type, id, payload, expected) => {
        const entity = mapWebEntityDocument({ type, id, name: `Example ${type}`, payload }, type);

        expect(entity).toMatchObject({ id, ...expected });
        expect(JSON.stringify(entity)).not.toContain('secret');
        expect(JSON.stringify(entity)).not.toContain('drop');
    });

    it('rejects a mismatched detail document type', () => {
        expect(() => mapWebEntityDocument({ type: 'user', id: 'usr_example', payload: {} }, 'world')).toThrow(
            'Unexpected entity type'
        );
    });
});
