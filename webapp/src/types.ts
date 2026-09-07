/** Shapes mirrored from the BcNuGetHelper backend. */

export interface AccessKey {
  name: string;
  key: string;
  feeds: string[];
  type: string | null;
  expires: string | null;
  description: string | null;
}

export interface MeResponse {
  login: string;
  name: string | null;
  avatarUrl: string | null;
  hasAccess: boolean;
}

export interface ConfigResponse {
  clientId: string;
}

export interface GitHubUser {
  login: string;
  name: string | null;
  avatar_url: string | null;
}

export type AccessKeyType = 'read' | 'write' | 'readwrite';

/** The feeds an access key can be scoped to. */
export const FEEDS = ['apps', 'runtime', 'symbols'] as const;
export type Feed = (typeof FEEDS)[number];

export interface CreateKeyInput {
  name: string;
  feeds: string[];
  type: AccessKeyType;
  description: string;
  expiresInDays: number | null;
}
