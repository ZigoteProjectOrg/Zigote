package com.zigote.gallery;

import org.libsdl.app.SDLActivity;

/**
 * Points SDL at the Zigote engine instead of its stock layout.
 *
 * <p>SDL's default is to load "SDL3" then "main" and call SDL_main. Our engine links SDL
 * statically into a single libzigote.so, and the managed host registers its app-main through
 * zigote_set_android_main before this activity starts — so the entry point SDL should call is
 * zigote_android_main.
 */
public class ZigoteActivity extends SDLActivity {
    @Override
    protected String[] getLibraries() {
        return new String[] { "zigote" };
    }

    @Override
    protected String getMainFunction() {
        return "zigote_android_main";
    }
}
