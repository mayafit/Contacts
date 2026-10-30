import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import RouteRoads from '../RouteRoads';
import type { RouteRoad } from '../types';

const road = (name: string, ref: boolean, m: number, fromM: number): RouteRoad => ({
  name,
  ref,
  m,
  fromM,
});

describe('RouteRoads', () => {
  it('lists the roads in travel order and finds every route on the one clicked', async () => {
    const onRoad = jest.fn();
    const { container } = render(
      <RouteRoads
        roads={[
          road('4', true, 5200, 0),
          road('431', true, 13100, 5200),
          road('דרך נמיר', false, 900, 18300),
        ]}
        onRoad={onRoad}
      />,
    );

    expect(container.textContent).toBe('Via4›431›דרך נמיר');
    fireEvent.click(screen.getByRole('button', { name: 'Road 431' }));
    expect(onRoad).toHaveBeenCalledWith('431');
    fireEvent.click(screen.getByRole('button', { name: 'דרך נמיר' }));
    expect(onRoad).toHaveBeenLastCalledWith('דרך נמיר');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('shows the first roads and the rest on demand', () => {
    const roads = Array.from({ length: 11 }, (_, i) =>
      road(`street ${i}`, false, 1000 - i, i * 1000),
    );
    render(<RouteRoads roads={roads} onRoad={jest.fn()} />);

    expect(screen.queryByRole('button', { name: 'street 10' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '+3 more' }));
    expect(screen.getByRole('button', { name: 'street 10' })).toBeTruthy();
  });

  it('shows nothing without roads', () => {
    const { container } = render(<RouteRoads roads={[]} onRoad={jest.fn()} />);
    expect(container.textContent).toBe('');
  });
});
