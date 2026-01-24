/**
 * @fileoverview Application version utilities
 * @module Contacts/utils/version
 *
 * Provides utilities for accessing and displaying the application version.
 * Uses CalVer format: YYmDD-build.N (m=month letter a-l)
 */

import versionData from '../../../../version.json';

/**
 * Version information interface
 */
export interface VersionInfo {
  /** CalVer version string (e.g., "26a24-build.1") */
  version: string;
  /** ISO timestamp when version was generated */
  timestamp: string;
  /** Description of the version format */
  format: string;
}

/**
 * Month letter mapping for display purposes
 */
const MONTH_NAMES: Record<string, string> = {
  a: 'January',
  b: 'February',
  c: 'March',
  d: 'April',
  e: 'May',
  f: 'June',
  g: 'July',
  h: 'August',
  i: 'September',
  j: 'October',
  k: 'November',
  l: 'December',
};

/**
 * Parses CalVer version string into components
 * @param version - Version string in format "YYmDD-build.N"
 * @returns Parsed components or null if invalid format
 */
export function parseCalVer(version: string): {
  year: number;
  month: string;
  monthName: string;
  day: number;
  buildNumber: number;
} | null {
  const match = version.match(/^(\d{2})([a-l])(\d{2})-build\.(\d+)$/);
  if (!match) {
    return null;
  }

  const [, yearStr, monthLetter, dayStr, buildStr] = match;

  return {
    year: 2000 + parseInt(yearStr, 10),
    month: monthLetter,
    monthName: MONTH_NAMES[monthLetter],
    day: parseInt(dayStr, 10),
    buildNumber: parseInt(buildStr, 10),
  };
}

/**
 * Gets the current application version
 * @returns Version string
 */
export function getVersion(): string {
  return versionData.version;
}

/**
 * Gets the full version information
 * @returns Version information object
 */
export function getVersionInfo(): VersionInfo {
  return versionData as VersionInfo;
}

/**
 * Gets a human-readable version string
 * @returns Formatted version string (e.g., "Build 1 - January 24, 2026")
 */
export function getReadableVersion(): string {
  const parsed = parseCalVer(versionData.version);

  if (!parsed) {
    return versionData.version;
  }

  return `Build ${parsed.buildNumber} - ${parsed.monthName} ${parsed.day}, ${parsed.year}`;
}

/**
 * Gets the build timestamp
 * @returns ISO timestamp string
 */
export function getBuildTimestamp(): string {
  return versionData.timestamp;
}
