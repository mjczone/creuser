import { config } from '@vue/test-utils';
import { Quasar } from 'quasar';

config.global.plugins = [...(config.global.plugins ?? []), [Quasar, {}]];
