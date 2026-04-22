/*
   BLAKE2 reference source code package - optimized C implementations

   Written in 2012 by Samuel Neves <sneves@dei.uc.pt>

   To the extent possible under law, the author(s) have dedicated all copyright
   and related and neighboring rights to this software to the public domain
   worldwide. This software is distributed without any warranty.

   You should have received a copy of the CC0 Public Domain Dedication along with
   this software. If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.
*/
#pragma once
#ifndef __BLAKE2_CONFIG_H__
#define __BLAKE2_CONFIG_H__

#if defined(_M_IX86_FP)
    #if _M_IX86_FP == 2
        #ifndef HAVE_SSE2
        #define HAVE_SSE2
        #endif
        #ifndef HAVE_AVX
            #define HAVE_AVX
        #endif
    #endif
#elif defined(_M_AMD64) || defined(_M_X64)
    #ifndef HAVE_SSSE3
    #define HAVE_SSSE3
    #endif
#endif

// These don't work everywhere
#if defined(__SSE2__) 
    #ifndef HAVE_SSE2
    #define HAVE_SSE2
    #endif
#endif

#if defined(__SSSE3__)
    #ifndef HAVE_SSSE3
    #define HAVE_SSSE3
    #endif
#endif

#if defined(__SSE4_1__)
    #ifndef HAVE_SSE41
    #define HAVE_SSE41
    #endif
#endif

#if defined(__AVX__) || defined(__AVX2__)
    #ifndef HAVE_AVX
    #define HAVE_AVX
    #endif
#endif

#if defined(__XOP__)
    #ifndef HAVE_XOP
    #define HAVE_XOP
    #endif
#endif


#ifdef HAVE_AVX2
    #ifndef HAVE_AVX
        #define HAVE_AVX
    #endif
#endif

#ifdef HAVE_XOP
    #ifndef HAVE_AVX
        #define HAVE_AVX
    #endif
#endif

#ifdef HAVE_AVX
    #ifndef HAVE_SSE41
        #define HAVE_SSE41
    #endif
#endif

#ifdef HAVE_SSE41
    #ifndef HAVE_SSSE3
        #define HAVE_SSSE3
    #endif
#endif

#ifdef HAVE_SSSE3
    #ifndef HAVE_SSE2
    #define HAVE_SSE2
    #endif
#endif

#if !defined(HAVE_SSE2)
    #error "This code requires at least SSE2."
#endif

#endif
