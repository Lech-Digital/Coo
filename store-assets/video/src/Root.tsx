import React from 'react';
import { Composition } from 'remotion';
import { Launch } from './Launch';
import { YouTube, YOUTUBE_FRAMES } from './YouTube';

export const RemotionRoot: React.FC = () => (
  <>
    <Composition id="Launch" component={Launch} durationInFrames={1080} fps={30} width={1080} height={1920} />
    <Composition id="YouTube" component={YouTube} durationInFrames={YOUTUBE_FRAMES} fps={30} width={1920} height={1080} />
  </>
);
