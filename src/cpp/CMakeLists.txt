set(
    _sms_library_sources
    "${CMAKE_CURRENT_SOURCE_DIR}/src/protocol.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/store.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/c_api.cpp")

if(WIN32)
    set(_sms_platform_source
        "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_windows.cpp")
    set(_sms_platform_definition SMS_PLATFORM_WINDOWS=1)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(_sms_platform_source
        "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_linux.cpp")
    set(_sms_platform_definition SMS_PLATFORM_LINUX=1)
else()
    message(
        FATAL_ERROR
            "SharedMemoryStore currently supports only Windows and Linux; detected '${CMAKE_SYSTEM_NAME}'."
    )
endif()

list(APPEND _sms_library_sources "${_sms_platform_source}")

set(
    _sms_public_headers
    "${CMAKE_CURRENT_SOURCE_DIR}/include/shared_memory_store/c_api.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/include/shared_memory_store/store.hpp")

function(_sms_configure_library target_name)
    target_sources(
        ${target_name}
        PRIVATE ${_sms_library_sources} ${_sms_public_headers})

    target_compile_features(${target_name} PUBLIC cxx_std_20)
    set_target_properties(
        ${target_name}
        PROPERTIES CXX_EXTENSIONS OFF
                   POSITION_INDEPENDENT_CODE ON
                   CXX_VISIBILITY_PRESET hidden
                   VISIBILITY_INLINES_HIDDEN YES
                   LINKER_LANGUAGE CXX)

    target_include_directories(
        ${target_name}
        PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
               $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
        PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")

    target_compile_definitions(
        ${target_name}
        PRIVATE SMS_BUILDING_LIBRARY=1 ${_sms_platform_definition})

    if(WIN32)
        target_compile_definitions(${target_name} PRIVATE WIN32_LEAN_AND_MEAN
                                                          NOMINMAX)
        if(MINGW)
            # Wheels and native archives must not depend on an unbundled
            # MinGW C++ runtime or unwinder DLL.
            target_link_options(${target_name} PRIVATE -static)
        endif()
    else()
        target_compile_definitions(${target_name} PRIVATE _FILE_OFFSET_BITS=64)
    endif()

    target_link_libraries(${target_name} PRIVATE Threads::Threads)

    if(MSVC)
        target_compile_options(${target_name} PRIVATE /W4 /permissive- /EHsc)
    else()
        target_compile_options(${target_name} PRIVATE -Wall -Wextra -Wpedantic)
    endif()
endfunction()

add_library(SharedMemoryStore SHARED)
add_library(SharedMemoryStore::SharedMemoryStore ALIAS SharedMemoryStore)
_sms_configure_library(SharedMemoryStore)
set_target_properties(
    SharedMemoryStore
    PROPERTIES EXPORT_NAME SharedMemoryStore
               OUTPUT_NAME shared_memory_store
               WINDOWS_EXPORT_ALL_SYMBOLS OFF)
if(WIN32)
    set_target_properties(SharedMemoryStore PROPERTIES PREFIX "")
endif()

# Native installations use the ABI major in the soname. Python wheels need a
# stable, unversioned filename for the standard-library loader.
if(NOT DEFINED SKBUILD AND SMS_PYTHON_INSTALL_DIR STREQUAL "")
    set_target_properties(
        SharedMemoryStore
        PROPERTIES VERSION "${PROJECT_VERSION}"
                   SOVERSION 1)
endif()

if(SMS_BUILD_STATIC)
    add_library(SharedMemoryStoreStatic STATIC)
    add_library(SharedMemoryStore::SharedMemoryStoreStatic ALIAS
                SharedMemoryStoreStatic)
    _sms_configure_library(SharedMemoryStoreStatic)
    target_compile_definitions(SharedMemoryStoreStatic PUBLIC SMS_STATIC=1)
    set_target_properties(
        SharedMemoryStoreStatic
        PROPERTIES EXPORT_NAME SharedMemoryStoreStatic
                   OUTPUT_NAME shared_memory_store_static)
endif()

if(SMS_INSTALL)
    install(
        TARGETS SharedMemoryStore
        EXPORT SharedMemoryStoreTargets
        RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}" COMPONENT Runtime
        LIBRARY DESTINATION "${CMAKE_INSTALL_LIBDIR}" COMPONENT Runtime
                NAMELINK_COMPONENT Development
        ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}" COMPONENT Development
        INCLUDES DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")

    if(SMS_BUILD_STATIC)
        install(
            TARGETS SharedMemoryStoreStatic
            EXPORT SharedMemoryStoreTargets
            ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}"
                    COMPONENT Development
            INCLUDES DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")
    endif()

    install(
        DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/include/"
        DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}"
        COMPONENT Development
        FILES_MATCHING
        PATTERN "*.h"
        PATTERN "*.hpp")

    if(NOT SMS_PYTHON_INSTALL_DIR STREQUAL "")
        install(
            TARGETS SharedMemoryStore
            RUNTIME DESTINATION "${SMS_PYTHON_INSTALL_DIR}" COMPONENT Python
            LIBRARY DESTINATION "${SMS_PYTHON_INSTALL_DIR}" COMPONENT Python
                    NAMELINK_SKIP)
    endif()
endif()
