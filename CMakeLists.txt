cmake_minimum_required(VERSION 3.20)

project(
    SharedMemoryStore
    VERSION 0.1.0
    DESCRIPTION "Cross-process shared memory value store"
    LANGUAGES CXX)

include(GNUInstallDirs)
include(CMakePackageConfigHelpers)
find_package(Threads REQUIRED)

option(SMS_BUILD_TESTS "Build the dependency-free native test suite" OFF)
option(SMS_BUILD_SAMPLES "Build native usage samples" OFF)
option(SMS_BUILD_STATIC "Build the optional static native library" OFF)

set(_sms_install_default ON)
if(NOT CMAKE_CURRENT_SOURCE_DIR STREQUAL CMAKE_SOURCE_DIR)
    set(_sms_install_default OFF)
endif()
option(SMS_INSTALL "Generate native install and CMake package rules"
       "${_sms_install_default}")
unset(_sms_install_default)
set(
    SMS_PYTHON_INSTALL_DIR
    ""
    CACHE STRING
          "Optional wheel-relative directory for the Python native library")

add_subdirectory(src/cpp)

if(SMS_INSTALL AND NOT SMS_PYTHON_INSTALL_DIR STREQUAL "")
    install(
        FILES
            "${CMAKE_CURRENT_SOURCE_DIR}/src/python/shared_memory_store/__init__.py"
            "${CMAKE_CURRENT_SOURCE_DIR}/src/python/shared_memory_store/_native.py"
            "${CMAKE_CURRENT_SOURCE_DIR}/src/python/shared_memory_store/enums.py"
            "${CMAKE_CURRENT_SOURCE_DIR}/src/python/shared_memory_store/store.py"
        DESTINATION "${SMS_PYTHON_INSTALL_DIR}"
        COMPONENT Python)
endif()

if(SMS_BUILD_TESTS)
    include(CTest)
    enable_testing()
    add_subdirectory(tests/cpp)
endif()

if(SMS_BUILD_SAMPLES)
    add_subdirectory(samples/CppBasicUsage)
endif()

if(SMS_INSTALL)
    set(
        SMS_INSTALL_CMAKEDIR
        "${CMAKE_INSTALL_LIBDIR}/cmake/SharedMemoryStore"
        CACHE STRING "Directory used to install the CMake package files")

    configure_package_config_file(
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/SharedMemoryStoreConfig.cmake.in"
        "${CMAKE_CURRENT_BINARY_DIR}/SharedMemoryStoreConfig.cmake"
        INSTALL_DESTINATION "${SMS_INSTALL_CMAKEDIR}")

    write_basic_package_version_file(
        "${CMAKE_CURRENT_BINARY_DIR}/SharedMemoryStoreConfigVersion.cmake"
        VERSION "${PROJECT_VERSION}"
        COMPATIBILITY SameMajorVersion)

    install(
        EXPORT SharedMemoryStoreTargets
        FILE SharedMemoryStoreTargets.cmake
        NAMESPACE SharedMemoryStore::
        DESTINATION "${SMS_INSTALL_CMAKEDIR}"
        COMPONENT Development)

    install(
        FILES
            "${CMAKE_CURRENT_BINARY_DIR}/SharedMemoryStoreConfig.cmake"
            "${CMAKE_CURRENT_BINARY_DIR}/SharedMemoryStoreConfigVersion.cmake"
        DESTINATION "${SMS_INSTALL_CMAKEDIR}"
        COMPONENT Development)
endif()
