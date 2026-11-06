import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const {
    entityResourceNames,
    validateEntityResourcePayload
} = require('../../../src-electron/serverEntityResourceValidation.js');

describe('server entity-resource validation', () => {
    it('accepts the one-shot author lookup and rejects the old repeated-page contract', () => {
        const payload = { name: 'userRemoteAvatars', arguments: { userId: 'usr_author' } };
        expect(validateEntityResourcePayload(payload)).toEqual(payload);
        expect(
            validateEntityResourcePayload({
                name: 'userRemoteAvatars',
                arguments: { userId: 'usr_author', n: 100, offset: 0 }
            })
        ).toBeNull();
    });

    it('accepts the exact original user-world tab parameters', () => {
        const payload = {
            name: 'userWorlds',
            arguments: {
                userId: 'usr_example',
                n: 50,
                offset: 100,
                sort: 'updated',
                order: 'descending',
                releaseStatus: 'public',
                user: ''
            }
        };

        expect(validateEntityResourcePayload(payload)).toEqual(payload);
        expect(entityResourceNames).toContain('userWorlds');
    });

    it('rejects unknown fields, arbitrary names and oversized paging', () => {
        expect(
            validateEntityResourcePayload({
                name: 'userGroups',
                arguments: { userId: 'usr_example', url: 'https://example.invalid' }
            })
        ).toBeNull();
        expect(validateEntityResourcePayload({ name: 'arbitraryUrl', arguments: {} })).toBeNull();
        expect(
            validateEntityResourcePayload({
                name: 'groupPosts',
                arguments: { groupId: 'grp_example', n: 101, offset: 0 }
            })
        ).toBeNull();
    });

    it('accepts only the member sort and role filters used by the original VRCX tab', () => {
        expect(
            validateEntityResourcePayload({
                name: 'groupMembers',
                arguments: {
                    groupId: 'grp_example',
                    n: 100,
                    offset: 0,
                    sort: 'joinedAt:desc',
                    roleId: 'grol_reader'
                }
            })
        ).not.toBeNull();
        expect(
            validateEntityResourcePayload({
                name: 'groupMembers',
                arguments: {
                    groupId: 'grp_example',
                    n: 100,
                    offset: 0,
                    sort: 'name:desc',
                    roleId: 'grol_reader'
                }
            })
        ).toBeNull();
    });

    it('does not accept private instance or nonce-bearing locations', () => {
        const base = { name: 'worldInstance' };
        expect(
            validateEntityResourcePayload({
                ...base,
                arguments: { worldId: 'wrld_example', instanceId: '12345~region(jp)' }
            })
        ).not.toBeNull();
        expect(
            validateEntityResourcePayload({
                ...base,
                arguments: {
                    worldId: 'wrld_example',
                    instanceId: '12345~private(usr_owner)~nonce(secret)'
                }
            })
        ).toBeNull();
    });
});
