import React from 'react';
import { Composition } from 'remotion';
import { Launch } from './Launch';

export const RemotionRoot: React.FC = () => (
  <Composition id="Launch" component={Launch} durationInFrames={960} fps={30} width={1080} height={1920} />
);
