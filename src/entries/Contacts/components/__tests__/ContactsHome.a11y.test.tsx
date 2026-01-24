/**
 * @fileoverview Accessibility tests for ContactsHome component
 * @module Contacts/components/__tests__/ContactsHome.a11y.test
 *
 * Example accessibility testing pattern using jest-axe.
 * Run these tests to ensure WCAG compliance.
 */

import React from 'react';
import { render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import { jest } from '@jest/globals';

/**
 * Simple test component to demonstrate accessibility testing pattern.
 * This component mimics basic UI patterns without MUI dependencies.
 */
const SimpleUserCard: React.FC<{
  name: string;
  email: string;
  onSignOut: () => void;
}> = ({ name, email, onSignOut }) => (
  <div role="region" aria-label="User information">
    <h1>Welcome, {name}</h1>
    <p>{email}</p>
    <button type="button" onClick={onSignOut} aria-label="Sign out of your account">
      Sign Out
    </button>
  </div>
);

describe('Accessibility Testing Pattern', () => {
  /**
   * Test: Component should have no accessibility violations
   * This is the primary accessibility test pattern using jest-axe.
   *
   * The axe() function runs automated accessibility checks including:
   * - Color contrast
   * - Missing alt text
   * - Missing form labels
   * - Improper heading hierarchy
   * - ARIA attribute usage
   *
   * Usage pattern for any component:
   * ```typescript
   * const { container } = render(<YourComponent />);
   * const results = await axe(container);
   * expect(results).toHaveNoViolations();
   * ```
   */
  it('should_HaveNoViolations_When_ProperlyStructured', async () => {
    const handleSignOut = jest.fn();

    const { container } = render(
      <SimpleUserCard name="Test User" email="test@example.com" onSignOut={handleSignOut} />,
    );

    const results = await axe(container);

    expect(results).toHaveNoViolations();
  });

  /**
   * Test: Buttons should have accessible names
   * Demonstrates testing specific accessibility requirements.
   */
  it('should_HaveAccessibleButton_When_AriaLabelProvided', async () => {
    const handleSignOut = jest.fn();

    const { container } = render(
      <SimpleUserCard name="Test User" email="test@example.com" onSignOut={handleSignOut} />,
    );

    // Verify button is accessible
    const button = screen.getByRole('button', { name: /sign out/i });
    expect(button).toBeInTheDocument();

    // Run full accessibility check
    const results = await axe(container);
    expect(results).toHaveNoViolations();
  });

  /**
   * Test: Content regions should have proper labeling
   * Demonstrates testing landmark regions.
   */
  it('should_HaveProperRegionLabeling_When_AriaLabelUsed', async () => {
    const handleSignOut = jest.fn();

    render(<SimpleUserCard name="Test User" email="test@example.com" onSignOut={handleSignOut} />);

    // Verify region is accessible by its aria-label
    const region = screen.getByRole('region', { name: /user information/i });
    expect(region).toBeInTheDocument();
  });
});

/**
 * Example showing how to test accessibility with axe config options
 */
describe('Accessibility with Custom Rules', () => {
  /**
   * Test: Can customize axe rules for specific testing scenarios
   * Use this pattern when you need to disable certain rules or
   * test specific WCAG criteria.
   */
  it('should_PassWithCustomRules_When_ConfiguredCorrectly', async () => {
    const { container } = render(
      <div>
        <h1>Heading Level 1</h1>
        <p>Content paragraph</p>
      </div>,
    );

    const results = await axe(container, {
      // Example: Only test certain rules
      rules: {
        'color-contrast': { enabled: true },
        'heading-order': { enabled: true },
      },
    });

    expect(results).toHaveNoViolations();
  });
});
