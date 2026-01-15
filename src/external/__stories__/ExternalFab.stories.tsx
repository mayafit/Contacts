import React from 'react';
import { Meta, StoryObj } from '@storybook/react';
import ExternalFab, { ExternalFabProps } from '../ExternalFab';

const meta: Meta<typeof ExternalFab> = {
  title: 'COMPONENTS/ExternalFab',
  component: ExternalFab,
  tags: ['autodocs'],
};

const dummyProps: ExternalFabProps = {
  label: 'M',
};

type Story = StoryObj<typeof ExternalFab>;

export const Default: Story = { args: dummyProps };

export const Red: Story = { args: { ...dummyProps, color: 'red' } };

const Template = (args) => <ExternalFab {...args} />;
export const Custom = Template.bind({});
// @ts-ignore
Custom.args = {
  ...dummyProps,
};

export default meta;
